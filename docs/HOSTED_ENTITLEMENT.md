# Hosted subscription verification

Live, History, and protected temporary audio use the verified subscription saved for the current Joe's Scanner account and authentication origin. A Standard grant permits the most recent 24 hours of History. A Premium grant permits the available archive. Independent recorders continue to use their own permissions.

A successful verification records its time, effective tier, and the earliest known expiration. Access ends at the earliest of 24 hours after that verification, a verified subscription or trial deadline, or a verified account credential deadline. The client accepts credential expiry from the authenticated `credential_expires_at` or `session.expires_at` fields when supplied. The checked-in plugin does not publish those fields or a firewall credential lifetime. Existing saved subscription state requires no schema change.

A timeout, connection failure, HTTP 408, HTTP 429, or HTTP 5xx uses the remaining verified grant for that account, if available. A transient response never advances the last successful verification time. A definitive denial clears the saved grant. Sign-out or an account or origin change retires outstanding checks and protected work. Local Only continues to block Joe-managed network requests. Authorized cached audio can still be checked locally.

Startup, recorder connections, foreground recovery, and History share the current account's in-flight verification. An individual caller can stop waiting while other callers remain. Successful results are reused for about 30 minutes during hosted monitoring. Transient outcomes use a short retry interval. An expired deadline cancels protected work even if a verification or media request is still in progress. Confirmed entitlement changes retire only affected operations and recorder generations. A tier change updates History access without resetting unrelated Live connections.

The recorder firewall still decides whether to serve a request. A client grant cannot issue credentials offline or change an authorization decision made by the firewall. A fresh process can reuse eligible account proof, with hosted transport still dependent on credentials the recorder accepts.

## Verification

The deterministic ServerAuthentication runner covers the 24-hour boundary, earlier verified expiry, transient and authoritative HTTP outcomes, first-time accounts, account changes, single-flight verification, cadence, protected native playback and media cancellation, and History tier changes.

```powershell
dotnet run --project tests/ServerAuthentication.Tests/ServerAuthentication.Tests.csproj --no-restore --disable-build-servers
```

The feature branch's disposable WordPress and Caddy TLS workflow covers existing plugin and API compatibility with synthetic accounts. Active SureCart subscriptions, deployed reverse-proxy and firewall behavior during an outage, and native device audio cancellation require a controlled environment with those services and devices.
