"""Exercise real WordPress directory forms and REST responses through HTTPS."""

from html.parser import HTMLParser
import json
import secrets


API = "/wp-json/joes-scanner/v1"
MY_SERVERS = "/my-servers/"
ADMIN = "/wp-admin/options-general.php?page=joes-scanner-auth&tab=server_directory"
POST = "/wp-admin/admin-post.php"
OWNER_ACTION = "joes_scanner_auth_submit_server"
ADMIN_REVIEW = "joes_scanner_auth_review_server_directory"
ADMIN_EDIT = "joes_scanner_auth_update_server_directory_entry"


def check(valid, message):
    if not valid:
        raise RuntimeError(message)


class Forms(HTMLParser):
    def __init__(self, content):
        super().__init__()
        self.forms = []
        self.active = None
        self.feed(content.decode("utf-8"))

    def handle_starttag(self, tag, attributes):
        attributes = dict(attributes)
        if tag == "form":
            self.active = {}
            self.forms.append(self.active)
        elif tag == "input" and self.active is not None and attributes.get("name"):
            self.active[attributes["name"]] = attributes.get("value", "")

    def handle_endtag(self, tag):
        if tag == "form":
            self.active = None


def form_data(content, action, **match):
    for form in Forms(content).forms:
        if form.get("action") == action and all(form.get(key) == (str(value) if value is not None else None)
                                                for key, value in match.items()):
            return {key: value for key, value in form.items()
                    if key in ("action", "_wpnonce", "_wp_http_referer", "id", "directory_op", "directory_version", "revision_id")}
    raise RuntimeError(f"No matching WordPress form for {action}.")


def browser_login(site, username, password, next_path):
    browser = site.opener(cookies=True)
    site.request("/wp-login.php", 200, opener=browser)
    site.request("/wp-login.php", 302, form=True, opener=browser, body={
        "log": username, "pwd": password, "testcookie": "1", "redirect_to": site.https + next_path,
    })
    return browser


def page(site, browser, path=MY_SERVERS):
    return site.request(path, 200, opener=browser)[1]


def post(site, browser, form, expected=302, **values):
    return site.request(POST, expected, form=True, opener=browser, body=form | values)


def owner_post(site, browser, op, expected=302, path=MY_SERVERS, **values):
    match = {"directory_op": op}
    if op == "save":
        match["id"] = None
    form = form_data(page(site, browser, path), OWNER_ACTION, **match)
    return post(site, browser, form, expected, **values)


def listing(site, browser, name):
    form = form_data(page(site, browser), OWNER_ACTION, directory_op="save", server_name=name)
    check(form.get("id", "").isdigit(), "The listing edit form must include the permanent directory ID.")
    return form


def review_form(site, admin, listing_id):
    content = page(site, admin, ADMIN + "&status=all")
    return form_data(content, ADMIN_REVIEW, id=listing_id)


def published(site):
    response = json.loads(site.request(API + "/servers", 200)[1])
    check(response.get("ok") is True, "The anonymous directory must remain available.")
    return {str(row["id"]): row for row in response["servers"]}, response["suspended"]


def fields(name, address, visibility="public", **other):
    return {
        "server_name": name, "server_url": address, "visibility": visibility, "map_anchor": "Boise, ID",
        "agreement_accept": "1", "area_label": "Test area", "description": "Synthetic directory fixture",
    } | other


def test_visibility(site, admin, alpha, beta, beta_name):
    approved = fields("Public recorder", "https://public.example.net", requires_scanner_login="1", requires_firewall_auth="1")
    owner_post(site, alpha, "save", path="/submit-server/", **approved)
    public_id = listing(site, alpha, "Public recorder")["id"]
    check(public_id not in published(site)[0], "A new Public listing became discoverable before approval.")
    check(b"Public recorder" not in page(site, beta), "The other account can see the pending public listing.")
    post(site, admin, review_form(site, admin, public_id), op="approve")
    current = published(site)[0][public_id]
    check(current["url"] == approved["server_url"] and current["requires_scanner_login"] is True
          and current["requires_firewall_auth"] is True and current["uses_api_firewall_credentials"] is False,
          "The public listing changed its stable ID or mixed the independent authentication flags.")
    found = page(site, None, "/public-servers/?directory_search=Synthetic")
    check(b"Public recorder" in found and b"Legacy public listing" not in found,
          "The website directory did not search descriptions.")

    private = fields("Owner-only recorder", "https://private.example.net", "private")
    owner_post(site, alpha, "save", **private)
    private_id = listing(site, alpha, "Owner-only recorder")["id"]
    check(b"Private \xc2\xb7 Approved" in page(site, alpha) and private_id not in published(site)[0],
          "Private listings must be available to their owners immediately.")
    check(b"Owner-only recorder" not in page(site, beta) and b"private.example.net" not in page(site, None),
          "A private listing or its address reached another viewer.")
    other = form_data(page(site, beta), OWNER_ACTION, directory_op="save")
    post(site, beta, other, 404, id=private_id, directory_version="1", **private)
    post(site, beta, other, 404, id=private_id, directory_op="remove")
    post(site, beta, review_form(site, admin, private_id), 403, op="remove")

    restricted = fields("Recipient recorder", "https://restricted.example.net", "restricted", audience=beta_name)
    owner_post(site, alpha, "save", **restricted)
    restricted_id = listing(site, alpha, "Recipient recorder")["id"]
    check(b"Recipient recorder" not in page(site, beta), "The recipient can see a submission that has yet to be reviewed.")
    post(site, admin, review_form(site, admin, restricted_id), op="approve")
    check(b"Recipient recorder" in page(site, beta) and b"Recipient recorder" not in page(site, None)
          and restricted_id not in published(site)[0], "Restricted visibility leaked or denied the approved recipient.")
    post(site, beta, other, 404, id=restricted_id, directory_op="remove")
    print("PASS Signed-in submission, stable IDs, Public review, Private ownership, Restricted recipients, and cross-account denial", flush=True)
    return public_id, approved


def test_revisions_and_suspension(site, compose, admin, alpha, beta, email, session_token, public_id, approved, secret):
    def credentials(url, expected, *, use_id=True):
        data = {"session_token": session_token, "server_url": url}
        if use_id:
            data["directory_id"] = public_id
        return site.request(API + "/server-firewall-credentials", expected, body=data)

    edit = form_data(page(site, admin, ADMIN + f"&edit_id={public_id}"), ADMIN_EDIT, id=public_id)
    post(site, admin, edit, **approved, use_api_firewall_credentials="1",
         firewall_username="synthetic-firewall", firewall_password=secret)
    headers, result = credentials(approved["server_url"], 200)
    check(json.loads(result)["password"] == secret and "no-store" in headers["Cache-Control"],
          "The privileged firewall path did not issue protected credentials to the approved endpoint.")
    credentials(approved["server_url"], 200, use_id=False)
    credentials("https://unapproved-origin.example.net", 404)
    check(secret not in json.dumps(published(site)), "A credential reached the public directory.")
    check(secret.encode() not in page(site, admin, ADMIN + f"&edit_id={public_id}"),
          "The administrator edit page rendered a saved credential.")

    edit = listing(site, alpha, approved["server_name"])
    minor = approved | {"server_name": "Public recorder revised", "description": "Edited descriptive copy"}
    post(site, alpha, edit, **minor)
    post(site, alpha, edit, 409, **minor)
    check(published(site)[0][public_id]["name"] == minor["server_name"], "A minor edit was not published immediately.")

    denied = minor | {"server_url": "https://rejected-address.example.net", "visibility": "private"}
    post(site, alpha, listing(site, alpha, minor["server_name"]), **denied)
    first_review = review_form(site, admin, public_id)
    check(b"Pending revision" in page(site, alpha) and b"rejected-address.example.net" not in page(site, beta)
          and published(site)[0][public_id]["url"] == approved["server_url"], "A pending revision broke the active public snapshot.")
    post(site, alpha, listing(site, alpha, minor["server_name"]), **(denied | {"description": "Changed proposal"}))
    post(site, admin, first_review, 409, op="approve")
    post(site, admin, review_form(site, admin, public_id), op="request_changes", note="Please confirm the intended visibility")
    check(b"Please confirm the intended visibility" in page(site, alpha), "The owner cannot see the change request.")
    post(site, alpha, listing(site, alpha, minor["server_name"]), **denied)
    post(site, admin, review_form(site, admin, public_id), op="reject", note="Keep the previous approved address")
    check(b"Keep the previous approved address" in page(site, alpha)
          and published(site)[0][public_id]["url"] == approved["server_url"], "Rejection changed the approved version or hid the explanation.")

    replacement = minor | {"server_url": "https://next-origin.example.net", "requires_scanner_login": "0"}
    post(site, alpha, listing(site, alpha, minor["server_name"]), **replacement)
    credentials(approved["server_url"], 200)
    credentials(replacement["server_url"], 404)
    post(site, admin, review_form(site, admin, public_id), op="approve")
    row = published(site)[0][public_id]
    check(row["url"] == replacement["server_url"] and row["requires_scanner_login"] is False
          and row["uses_api_firewall_credentials"] is False,
          "Approval failed to transfer the stable listing or clear credentials tied to the previous origin.")
    credentials(approved["server_url"], 404)
    credentials(replacement["server_url"], 404)
    print("PASS Minor and staged edits, stale review denial, rejection explanations, atomic approval, and firewall origin binding", flush=True)

    post(site, alpha, listing(site, alpha, minor["server_name"]), directory_op="remove")
    check(public_id not in published(site)[0], "An owner removal did not immediately hide the listing.")
    removed = listing(site, alpha, minor["server_name"])
    check(removed["id"] == public_id, "Ordinary removal discarded the stable listing record.")
    post(site, alpha, removed, **replacement)
    post(site, admin, review_form(site, admin, public_id), op="approve")
    post(site, admin, review_form(site, admin, public_id), op="suspend", note="Synthetic moderation note")
    rows, suspensions = published(site)
    check(public_id not in rows and {"id": int(public_id), "status": "suspended"} in suspensions
          and "Synthetic moderation note" not in json.dumps(suspensions), "The safe client suspension status is missing or contains a private note.")
    for blocked in ("HTTPS://NEXT-ORIGIN.EXAMPLE.NET:443/", "http://public.example.net/"):
        owner_post(site, beta, "save", 409, **fields("Renamed recorder", blocked))
    post(site, admin, review_form(site, admin, "21"), op="suspend", note="Synthetic legacy hold")
    owner_post(site, beta, "save", 409, **fields("Legacy alias attempt", "https://legacy-history.example.net/"))
    post(site, admin, review_form(site, admin, public_id), op="reinstate")
    check(public_id not in published(site)[0] and {"id": int(public_id), "status": "suspended"} not in published(site)[1],
          "Reinstatement must permit review without silently republishing the listing.")
    post(site, alpha, listing(site, alpha, minor["server_name"]), **replacement)
    post(site, admin, review_form(site, admin, public_id), op="approve")
    check(public_id in published(site)[0], "Administrator reinstatement did not allow resubmission.")
    compose.fixture("directory-audit", {"id": public_id, "ownerEmail": email, "secret": secret})
    print("PASS Removal, durable security suspension, historical URLs after migration, reinstatement, and audit trail", flush=True)


def test_verification(site, compose, alpha_email):
    def issue(browser, email, expected=302):
        owner_post(site, browser, "issue", expected, email=email)
        if expected == 302:
            return compose.fixture("directory-mail", {"email": email}).strip()
        return None

    def verify(browser, email, code, expected=302):
        return owner_post(site, browser, "verify", expected, email=email, code=code)

    existing = site.opener(cookies=True)
    response, body = site.request(MY_SERVERS, 200, opener=existing)
    check("no-store" in response["Cache-Control"] and b"Reset WordPress password" in body,
          "The verification page must disable caching and provide WordPress password recovery.")
    code = issue(existing, alpha_email.upper())
    verify(site.opener(cookies=True), alpha_email, code, 400)
    headers, _ = verify(existing, alpha_email, code)
    cookies = headers.get_all("Set-Cookie", [])
    check(any("__Host-js-directory-session" in cookie and "secure" in cookie.lower()
              and "httponly" in cookie.lower() for cookie in cookies)
          and not any("wordpress_logged_in" in cookie for cookie in cookies),
          "Verification must grant only a secure directory cookie.")
    check(b"Owner-only recorder" in page(site, existing), "Verification failed to associate the existing account.")
    site.request(ADMIN, 302, opener=existing)
    owner_post(site, existing, "end")
    verify(existing, alpha_email, code, 400)

    created_email = "new_directory_owner@example.invalid"
    created = site.opener(cookies=True)
    expired = issue(created, created_email)
    compose.fixture("directory-expire", {"email": created_email})
    verify(created, created_email, expired, 400)
    verify(created, created_email, issue(created, created_email))
    owner_post(site, created, "save", **fields("Email-created private listing", "https://email-owner.example.net", "private"))
    check(b"Email-created private listing" in page(site, created) and b"Recipient recorder" not in page(site, created),
          "The new account did not own its Private listing or gained another account's Restricted access.")

    administrator = site.opener(cookies=True)
    admin_email = "integration_admin@example.invalid"
    verify(administrator, admin_email, issue(administrator, admin_email))
    site.request(ADMIN, 302, opener=administrator)

    limited = site.opener(cookies=True)
    limited_email = "rate_limited@example.invalid"
    code = issue(limited, limited_email)
    wrong = "00000000" if code != "00000000" else "00000001"
    for _ in range(5):
        verify(limited, limited_email, wrong, 400)
    verify(limited, limited_email, code, 429)
    issue(site.opener(cookies=True), limited_email)
    issue(site.opener(cookies=True), limited_email.upper())
    issue(site.opener(cookies=True), limited_email, 429)
    compose.fixture("directory-accounts", {"existingEmail": alpha_email, "createdEmail": created_email})
    print("PASS Controlled email, account reuse and creation, expiry, browser binding, one-time codes, and issuance and verification limits", flush=True)


def check_directory(site, compose, admin_login, admin_password, alpha_user, beta_user, session_token):
    compose.fixture("directory-pages", {"existingEmail": alpha_user["email"]})
    admin = browser_login(site, admin_login, admin_password, ADMIN)
    alpha = browser_login(site, alpha_user["username"], alpha_user["password"], MY_SERVERS)
    beta = browser_login(site, beta_user["username"], beta_user["password"], MY_SERVERS)
    public_id, approved = test_visibility(site, admin, alpha, beta, beta_user["username"])
    secret = "directory-fixture-" + secrets.token_hex(18)
    for url in ("file:///data", "http://127.0.0.1", "https://[::1]", "https://server.example.net:0",
                "https://user:" + secret + "@server.example.net", "https://server.example.net?api_key=" + secret):
        owner_post(site, alpha, "save", 400, **fields("Invalid recorder", url))
    owner_post(site, alpha, "save", 400, **fields("Invalid credentials", "https://invalid.example.net", firewall_password=secret))
    check("Invalid recorder" not in json.dumps(published(site)[0]), "An invalid submission became public.")
    print("PASS Invalid schemes, local addresses, invalid ports, and embedded or posted directory credentials are rejected", flush=True)
    test_revisions_and_suspension(site, compose, admin, alpha, beta, alpha_user["email"], session_token,
                                  public_id, approved, secret)
    test_verification(site, compose, alpha_user["email"])
    return secret
