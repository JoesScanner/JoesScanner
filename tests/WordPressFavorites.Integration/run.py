"""Run the disposable WordPress, Favorites, and server directory integration suite."""

import argparse
import base64
import concurrent.futures
import hashlib
import http.cookiejar
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from directory_checks import check_directory


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
API = "/wp-json/joes-scanner/v1"
PLUGIN = "joes-scanner-auth"
TITLE = "JoesScanner Favorites Integration"


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def command(arguments, label, *, env=None, data=None, required=True, cwd=HERE):
    result = subprocess.run(arguments, cwd=cwd, env=env, input=data, capture_output=True,
                            text=True, encoding="utf-8", errors="replace", timeout=360, check=False)
    if required:
        # Output may contain generated credentials, especially during core install.
        check(result.returncode == 0, f"{label} failed (exit code {result.returncode}).")
    return result


def check_environment():
    check(shutil.which("docker"), "A local Docker Engine with Compose v2 is required. Docker was not found.")
    check(shutil.which("dotnet"), "The repository's .NET SDK is required for the client integration test.")
    context = command(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
                      "Inspect Docker context").stdout.strip()
    # DOCKER_CONTEXT overrides DOCKER_HOST when both are set.
    host = context if os.environ.get("DOCKER_CONTEXT") else os.environ.get("DOCKER_HOST") or context
    check(host.startswith(("unix:///", "npipe:////./pipe/")),
          "Select a local Docker socket or Windows named pipe for this disposable test.")
    command(["docker", "compose", "version"], "Check Docker Compose v2")
    engine = command(["docker", "info", "--format", "{{.OSType}}"], "Check local Docker Engine").stdout.strip()
    check(engine == "linux", "The integration runner requires a local Linux-container Docker Engine.")


def wait_until(label, condition, seconds=45):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        result = condition()
        if result:
            return result
        time.sleep(0.5)
    raise RuntimeError(f"Timed out waiting for {label}.")


class Compose:
    def __init__(self, https_port, http_port):
        self.project = "joesscanner-favorites-test-" + uuid.uuid4().hex[:10]
        self.env = os.environ | {
            "JS_FAVORITES_DB_PASSWORD": secrets.token_hex(24),
            "JS_FAVORITES_HTTPS_PORT": str(https_port),
            "JS_FAVORITES_HTTP_PORT": str(http_port),
        }

    def run(self, *args, label, data=None, required=True):
        return command(["docker", "compose", "--project-name", self.project, "--file", str(HERE / "compose.yaml"), *args],
                       label, env=self.env, data=data, required=required)

    def wp(self, *args, label, data=None):
        return self.run("run", "--rm", "-T", "cli", "wp", *args, label=label, data=data)

    def fixture(self, operation, data=None):
        return self.wp("eval-file", "/fixtures.php", operation, label=f"WordPress fixture {operation}",
                       data=json.dumps(data or {})).stdout


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class WordPress:
    def __init__(self, https_port, http_port, ca_path):
        self.https = f"https://localhost:{https_port}"
        self.http = f"http://localhost:{http_port}"
        self.ssl = ssl.create_default_context(cafile=ca_path)
        self.api = self.opener()

    def opener(self, cookies=False):
        handlers = [urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=self.ssl), NoRedirect()]
        if cookies:
            handlers.append(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        return urllib.request.build_opener(*handlers)

    def request(self, path, expected, *, body=None, authorization=None, tls=True, form=False, opener=None):
        check(path.startswith("/") and not path.startswith("//"), "Expected a local request path.")
        check(tls or authorization is None, "The integration fixture only sends credentials over HTTPS.")
        headers = {}
        if authorization is not None:
            headers["Authorization"] = authorization
        if body is not None:
            headers["Content-Type"] = "application/x-www-form-urlencoded" if form else "application/json"
            if isinstance(body, dict):
                body = (urllib.parse.urlencode(body) if form else json.dumps(body)).encode("utf-8")
        url = (self.https if tls else self.http) + path
        try:
            reply = (opener or self.api).open(urllib.request.Request(url, body, headers), timeout=20)
        except urllib.error.HTTPError as error:
            reply = error
        with reply:
            check(reply.code == expected, f"{path.partition('?')[0]} returned HTTP {reply.code}; expected {expected}.")
            return reply.headers, reply.read()

    def sync(self, account=None, *, owner=None, changes=(), after=None, expected=200, body=None, tls=True,
             authorization=None):
        if body is None:
            body = {"ownerId": owner, "changes": list(changes), "after": after}
        if account is not None:
            authorization = account_header(account)
        headers, content = self.request(API + "/favorites/sync", expected, body=body, authorization=authorization, tls=tls)
        if expected == 200:
            cache = headers.get("Cache-Control", "").lower()
            check("no-store" in cache and "private" in cache, "Favorites responses must disable shared caching.")
        return json.loads(content)


def account_header(account, by_email=False):
    login = account["email" if by_email else "username"]
    pair = (login + ":" + account["password"]).encode("utf-8")
    return "JoesScannerAccount " + base64.b64encode(pair).decode("ascii")


def account(name):
    return {"username": name, "email": name + "@example.invalid",
            "password": secrets.token_hex(12) + r"""-\Test'Quote":+é"""}


def change(value, revision=0, deleted=False):
    return {"mutationId": uuid.uuid4().hex, "expectedRevision": revision, "value": value, "deleted": deleted}


def group(label):
    return {"id": "g" + uuid.uuid4().hex, "label": label}


def favorite(index, group_id=None):
    server = "d:978"
    identity = {"system": "id:ARCHIVE", "site": "id:SITE", "receiver": "label:RX", "talkgroup": f"id:{index}"}
    key = "f" + hashlib.sha256("\x1f".join([server, *identity.values()]).encode("utf-8")).hexdigest()
    return {"id": key, "serverKey": server, "identity": identity, "label": f"Dispatch {index}", "groupId": group_id}


def record(page, key):
    return next((item for item in page["records"] if item["value"]["id"] == key), None)


def check_authentication(site, alpha, beta):
    denied = site.sync(expected=403, tls=False)
    check(denied["code"] == "joes_favorites_https_required", "The plaintext route must refuse the request in WordPress.")
    for header in (None, "Basic c3ludGhldGlj", "JoesScannerAccount %%%"):
        denied = site.sync(expected=401, authorization=header)
        check(denied["code"] == "joes_favorites_authentication_required", "The custom scheme must fail closed.")

    users = []
    for user in (alpha, beta):
        by_name = site.sync(user)["userId"]
        by_email = site.sync(authorization=account_header(user, by_email=True))["userId"]
        check(isinstance(by_name, int) and by_name > 0 and by_email == by_name,
              "WordPress username and email must resolve to the same user ID.")
        users.append(by_name)
    check(users[0] != users[1], "The two accounts must have distinct WordPress IDs.")
    print("PASS HTTPS-only Favorites, forwarded authorization, and free WordPress username/email logins", flush=True)
    return users


def check_cross_account(site, alpha, beta, owner_a, owner_b):
    value = group("Account B private group")
    original = change(value)
    stored = site.sync(beta, owner=owner_b, changes=[original])
    check(stored["results"][0]["accepted"] and record(stored, value["id"])["revision"] == 1,
          "Account B could not create its private record.")

    site.sync(alpha, owner=owner_b, expected=403)
    site.sync(alpha, owner=owner_b, changes=[change(value, 1)], expected=403)
    site.sync(alpha, owner=owner_b, changes=[change({"id": value["id"]}, 1, deleted=True)], expected=403)
    spoof = {"ownerId": owner_a, "_favorites_authenticated_user": owner_b, "userId": owner_b, "changes": []}
    own = site.sync(alpha, body=spoof)
    check(own["userId"] == owner_a and record(own, value["id"]) is None,
          "A submitted ownership field cannot expose a second user's records.")

    for write in (change(value | {"label": "Other account"}, 1), change({"id": value["id"]}, 1, deleted=True)):
        outcome = site.sync(alpha, owner=owner_a, changes=[write])["results"][0]
        check(not outcome["accepted"] and outcome["record"] is None, "A foreign ID cannot be changed or disclosed.")
    check(record(site.sync(beta), value["id"])["value"] == value, "Account A changed Account B's data.")
    print("PASS Authenticated user IDs isolate reads, writes, and deletions", flush=True)


def check_mutations(site, alpha, owner):
    value = group("Concurrent dispatch tests")
    key = value["id"]
    initial = change(value)
    for attempt in (initial, initial):
        outcome = site.sync(alpha, owner=owner, changes=[attempt])["results"][0]
        check(outcome["accepted"] and outcome["record"]["revision"] == 1, "A duplicate create advanced the revision.")

    barrier = threading.Barrier(2, timeout=10)

    def race(write):
        barrier.wait()
        return site.sync(alpha, owner=owner, changes=[write])["results"][0]

    choices = [change(value | {"label": label}, 1) for label in ("First edit", "Second edit")]
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as workers:
        outcomes = list(workers.map(race, choices))
    winners = [outcome for outcome in outcomes if outcome["accepted"]]
    check(len(winners) == 1 and winners[0]["record"]["revision"] == 2,
          "Only one conditional update can own the next revision.")
    winner = next(write for write in choices if write["mutationId"] == winners[0]["mutationId"])
    repeated = site.sync(alpha, owner=owner, changes=[winner])["results"][0]
    check(repeated["accepted"] and repeated["record"]["revision"] == 2, "A duplicate update advanced the revision.")

    deletion = change({"id": key}, 2, deleted=True)
    deleted = site.sync(alpha, owner=owner, changes=[deletion])["results"][0]
    check(deleted["accepted"] and deleted["record"]["deleted"] and deleted["record"]["value"] == {"id": key},
          "Deletion must keep a revisioned tombstone without record contents.")
    stale = [initial, change(value, 2), change(value)]
    for attempt in stale:
        outcome = site.sync(alpha, owner=owner, changes=[attempt])["results"][0]
        check(not outcome["accepted"] and outcome["record"]["revision"] == 3 and outcome["record"]["deleted"],
              "A stale mutation or create resurrected a tombstone.")
    print("PASS Conditional concurrency, mutation retries, and tombstones", flush=True)


def check_pagination(site, alpha, owner):
    named_group = group("Pagination dispatch")
    values = [named_group, *(favorite(index, named_group["id"]) for index in range(207))]
    for start in range(0, len(values), 50):
        batch = [change(value) for value in values[start:start + 50]]
        result = site.sync(alpha, owner=owner, changes=batch)
        check(len(result["results"]) == len(batch) and all(item["accepted"] for item in result["results"]),
              "A valid group or Favorites batch was rejected.")

    keys = []
    after = None
    for _ in range(4):
        page = site.sync(alpha, owner=owner, after=after)
        page_keys = [item["value"]["id"] for item in page["records"]]
        check(len(page_keys) <= 200 and page_keys == sorted(set(page_keys)), "A Favorites page is misordered or oversized.")
        check(after is None or not page_keys or after < page_keys[0], "A second page overlapped the cursor.")
        keys.extend(page_keys)
        after = page["next"]
        if after is None:
            break
        check(len(page_keys) == 200 and after == page_keys[-1], "The next cursor must be the last returned record.")
    check(after is None and len(keys) > 200 and set(value["id"] for value in values) <= set(keys),
          "The complete Favorite and group list did not survive pagination.")
    print("PASS 207 Favorites and a group across cursor pages", flush=True)


def check_payload_validation(site, alpha, owner, secret):
    invalid = [b'{"changes":', {"changes": {}}, {"changes": {"0": change(group("Not a list"))}, "ownerId": owner},
               {"changes": [], "after": "invalid"}, {"changes": [], "padding": "x" * 262145},
               {"changes": [change(group(str(index))) for index in range(51)], "ownerId": owner}]
    for body in invalid:
        site.sync(alpha, body=body, expected=400)
    candidate = group("Batch should be rejected")
    site.sync(alpha, owner=owner, changes=[change(candidate), change({"id": "bad"})], expected=400)
    site.sync(alpha, changes=[change(candidate)], expected=400)
    site.sync(alpha, owner=owner, changes=[change(candidate), change(candidate)], expected=400)
    check(record(site.sync(alpha, after="g" + "0" * 32), candidate["id"]) is None,
          "An invalid batch must not write a leading valid record.")

    clean = favorite(500)
    polluted = clean | {"password": secret, "accessToken": secret, "recorderCredentials": secret,
                        "identity": clean["identity"] | {"privateCredential": secret}}
    outcome = site.sync(alpha, owner=owner, changes=[change(polluted)])["results"][0]
    check(outcome["accepted"] and outcome["record"]["value"] == clean, "Favorites must write only their public data fields.")
    invalid_server = clean | {"serverKey": "https://fixture:" + secret + "@recorder.example.invalid"}
    site.sync(alpha, owner=owner, changes=[change(invalid_server, 1)], expected=400)
    print("PASS Request shape, bounded payloads, complete batch validation, and secret-field exclusion", flush=True)


def check_rate_limit(site):
    invalid_login = account("rate-" + uuid.uuid4().hex[:12])
    for _ in range(20):
        site.sync(invalid_login, expected=401)
    blocked = site.sync(invalid_login, expected=429)
    check(blocked["code"] == "joes_favorites_rate_limited", "The synthetic failure budget did not enforce rate limiting.")
    print("PASS Local rate limit rejected the next invalid attempt", flush=True)


def check_legacy(site, compose, admin, admin_password, alpha, beta, owners):
    # The stock fixture deliberately has no SureCart plugin or account.
    payload = {"username": alpha["username"], "password": alpha["password"], "app_version": "99.0"}
    _, missing = site.request(API + "/auth", 500, body=payload)
    check(json.loads(missing)["error"] == "surecart_not_available", "The legacy hosted entitlement guard changed.")

    compose.fixture("legacy-settings", {"surecartEnabled": True})
    browser = site.opener(cookies=True)
    admin_page = "/wp-admin/options-general.php?page=joes-scanner-auth&tab=surecart"
    site.request("/wp-login.php", 200, opener=browser)
    site.request("/wp-login.php", 302, form=True, opener=browser, body={
        "log": admin, "pwd": admin_password, "testcookie": "1", "redirect_to": site.https + admin_page
    })
    _, enabled_page = site.request(admin_page, 200, opener=browser)
    settings_field = b"joes_scanner_auth_options[allowed_statuses][]"
    check(settings_field in enabled_page, "The enabled SureCart tab must load its status settings.")

    compose.fixture("legacy-settings", {"surecartEnabled": False})
    _, disabled_page = site.request(admin_page, 200, opener=browser)
    check(b"SureCart integration is disabled." in disabled_page and settings_field not in disabled_page,
          "The disabled SureCart tab must show the disabled state.")
    session_tokens = []
    for user, expected_owner in zip((alpha, beta), owners, strict=True):
        for login in (user["username"], user["email"]):
            _, content = site.request(API + "/auth", 200, body={
                "username": login, "password": user["password"], "app_version": "99.0"
            })
            response = json.loads(content)
            check(response["ok"] and response["user"]["id"] == expected_owner and response["subscription"]["status"] == "disabled"
                  and response["session_token"], "Legacy WordPress authentication or subscription response compatibility changed.")
            session_tokens.append(response["session_token"])

    print("PASS Legacy auth, missing/disabled SureCart behavior, and the WordPress admin settings page", flush=True)
    return session_tokens


def prepare(compose, directory, https_port, http_port):
    compose.run("up", "-d", "db", "wordpress", "tls", label="Start disposable WordPress and MariaDB")
    wait_until("WordPress files", lambda: compose.run(
        "exec", "-T", "wordpress", "test", "-s", "/var/www/html/wp-config.php",
        label="Check WordPress startup", required=False).returncode == 0)
    certificate = wait_until("loopback root certificate", lambda: compose.run(
        "exec", "-T", "tls", "cat", "/data/caddy/pki/authorities/local/root.crt",
        label="Read test CA", required=False).stdout.strip())
    ca_path = directory / "loopback-ca.crt"
    ca_path.write_text(certificate + "\n", encoding="ascii")

    site = WordPress(https_port, http_port, ca_path)
    admin = "integration_admin"
    admin_password = secrets.token_urlsafe(24)
    compose.wp("core", "install", "--url=" + site.https, "--title=" + TITLE, "--admin_user=" + admin,
               "--admin_password=" + admin_password, "--admin_email=integration_admin@example.invalid", "--skip-email",
               label="Install WordPress locally")
    compose.wp("rewrite", "structure", "/%postname%/", label="Enable local REST paths")
    compose.run("exec", "-T", "wordpress", "sh", "-ec", "for file in /var/www/html/wp-content/plugins/joes-scanner-auth/*.php /var/www/html/wp-content/plugins/joes-scanner-auth/includes/*.php /fixtures.php /var/www/html/wp-content/mu-plugins/js-directory-test-mail.php; do php -l \"$file\"; done",
                label="Lint plugin and test PHP")
    compose.wp("plugin", "activate", PLUGIN, label="Activate Joe's Scanner Auth API")
    compose.fixture("schema")
    for operation in ("upgrade-from-1.5.8", "missing-table", "upgrade-from-1.5.9"):
        compose.fixture(operation)
        try:
            _, index = site.request("/wp-json/", 200)
            check(API.replace("/wp-json", "") + "/favorites/sync" in json.loads(index)["routes"], "The plugin did not register Favorites.")
            _, directory_data = site.request(API + "/servers", 200)
            check(json.loads(directory_data).get("ok") is True, "The plugin did not return its directory after the schema change.")
        except RuntimeError as error:
            raise RuntimeError(f"{operation}: {error}") from error
        compose.fixture("schema")
    compose.fixture("directory-schema")
    print("PASS Plugin activation, previous schema upgrades to 1.5.10, legacy directory ownership and URLs, and Favorites table repair", flush=True)
    return site, ca_path, admin, admin_password


class SyntheticRecorder(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path != "/calls" or "Authorization" in self.headers:
            self.send_error(400)
            return
        body = b"Synthetic recorder call"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def check_client(site, ca_path, alpha, beta, directory, hold):
    with ThreadingHTTPServer(("127.0.0.1", 0), SyntheticRecorder) as recorder:
        thread = threading.Thread(target=recorder.serve_forever, daemon=True)
        thread.start()
        try:
            fixture = directory / "synthetic-client-fixture.json"
            recorder_url = f"http://127.0.0.1:{recorder.server_port}"
            fixture.write_text(json.dumps({"accountUrl": site.https, "caPath": str(ca_path),
                                          "recorderUrl": recorder_url, "alpha": alpha, "beta": beta}), encoding="utf-8")
            suite = "tests/ServerAuthentication.Tests/ServerAuthentication.Tests.csproj"
            result = command(["dotnet", "run", "--project", suite, "--", "--wordpress-favorites", str(fixture)],
                             "Real WordPress two-database client test", cwd=REPO)
            for line in result.stdout.splitlines():
                if line.startswith("PASS "):
                    print(line, flush=True)
            if hold:
                print(f"Loopback WordPress {site.https}")
                print(f"Temporary test CA and synthetic account details are in {fixture}")
                input("Press Enter to stop and remove the disposable test site. ")
        finally:
            recorder.shutdown()
            thread.join()


def exercise(compose, directory, arguments):
    site, ca_path, admin, admin_password = prepare(compose, directory, arguments.https_port, arguments.http_port)
    alpha, beta = account("integration_alpha"), account("integration_beta")
    accounts = {"alpha": alpha, "beta": beta}
    compose.fixture("seed", accounts)
    compose.fixture("auth-policy", accounts)
    owners = check_authentication(site, alpha, beta)
    check_cross_account(site, alpha, beta, *owners)
    check_mutations(site, alpha, owners[0])
    check_pagination(site, alpha, owners[0])
    marker = "fixture-" + secrets.token_hex(18)
    check_payload_validation(site, alpha, owners[0], marker)
    check_rate_limit(site)
    tokens = check_legacy(site, compose, admin, admin_password, alpha, beta, owners)
    directory_secret = check_directory(site, compose, admin, admin_password, alpha, beta, tokens[0])
    check_client(site, ca_path, alpha, beta, directory, arguments.hold)

    redact = [marker, admin_password, directory_secret, *tokens]
    for user in (alpha, beta):
        redact.extend([user["password"].split("-", 1)[0], account_header(user).split(" ", 1)[1],
                       account_header(user, by_email=True).split(" ", 1)[1]])
    compose.fixture("storage", {"redact": redact})
    php = compose.run("exec", "-T", "wordpress", "sh", "-c",
                      "if test -f /tmp/js-favorites-integration.log; then cat /tmp/js-favorites-integration.log; fi",
                      label="Read disposable PHP diagnostics").stdout
    web = compose.run("logs", "--no-color", "wordpress", "tls", label="Read disposable web diagnostics").stdout
    check(all(secret not in php + web for secret in redact), "A synthetic secret reached the PHP or web diagnostic log.")
    print("PASS Favorites storage and WordPress/proxy diagnostic logs exclude synthetic secrets", flush=True)


def report_php_errors(compose):
    # Error context can contain request data or SQL values. Report only generic
    # error identifiers and source locations from the disposable site's log.
    result = compose.run("exec", "-T", "wordpress", "sh", "-c",
                         "test ! -f /tmp/js-favorites-integration.log || tail -n 250 /tmp/js-favorites-integration.log",
                         label="Collect disposable PHP error locations", required=False)
    for line in result.stdout.splitlines():
        category = re.search(r"PHP (?:Fatal error|Parse error|Warning): (?:Uncaught ([A-Za-z_\\]+): )?", line)
        location = re.search(r"(/var/www/html/[a-zA-Z0-9_/.-]+\.php)(?::|\(| on line )(\d+)", line)
        if not category or not location:
            continue
        missing = re.search(r"Call to undefined (?:function|method) ([a-zA-Z_\\:][a-zA-Z0-9_\\:]*)\(", line)
        detail = f" undefined {missing[1]}" if missing else ""
        print(f"PHP diagnostic {category[1] or 'error'}{detail} at {location[1]}:{location[2]}", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check the local tooling without creating containers.")
    parser.add_argument("--hold", action="store_true", help="Keep the site open for a Windows smoke test until Enter.")
    parser.add_argument("--https-port", type=int, default=18443)
    parser.add_argument("--http-port", type=int, default=18080)
    arguments = parser.parse_args()
    try:
        check_environment()
        if arguments.check:
            print("Local Linux-container Docker Engine, Compose v2 and .NET SDK are available.")
            return 0
        ports = (arguments.https_port, arguments.http_port)
        check(all(1024 <= port <= 65535 for port in ports) and ports[0] != ports[1], "Choose two distinct unprivileged ports.")
        compose = Compose(*ports)
        print(f"Disposable Compose project {compose.project}", flush=True)
        with tempfile.TemporaryDirectory(prefix="joesscanner-favorites-") as temporary:
            try:
                exercise(compose, Path(temporary), arguments)
            except (RuntimeError, OSError, ValueError):
                report_php_errors(compose)
                raise
            finally:
                compose.run("down", "--volumes", "--remove-orphans", label="Remove this disposable project")
        return 0
    except (RuntimeError, OSError, ValueError, subprocess.TimeoutExpired) as error:
        # A subprocess error can include fixture arguments. Fall back to its type.
        safe = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print(f"Integration unavailable or failed. {safe}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
