# Security

How Lookout decides who can use it, and why it works the way it does. The code documents what each piece does; this document explains the reasoning and the
constraints that must stay true as Lookout changes. Terms such as **Owner** are defined in [`GLOSSARY.md`](../GLOSSARY.md).

## Threat model

Uploading a watch script is the same as running code on the Runner's host: the Runner executes it with Node. So anyone who can use the API can read the
`.env` file, the database and anything else the Runner's user can reach. Everything below exists to make sure only the Owner can use the API, and only
the Backend can ask the Runner to run a script.

Lookout runs on a LAN or tailnet today, but it's designed as if it were exposed to the internet. It defends against:

- **Anyone who can reach the Backend**, who must sign in first.
- **Malicious pages in the Owner's browser**: other sites, and, more importantly, other apps on the same site, such as a sibling subdomain in a homelab or
  another dev server on `localhost`. These are the cross-site request forgery (CSRF) cases.
- **Password guessing**, by rate limiting.

It doesn't defend against a compromised Owner browser (XSS in Lookout itself defeats every check here), a compromised host, or a malicious watch script:
scripts aren't sandboxed yet, which is why there is only one Owner ([ADR 0001](adr/0001-single-owner.md)).

## Requirements on how Lookout is served

These aren't visible in any one file, and breaking them breaks signing in or weakens the checks:

- **HTTPS everywhere except `localhost`.** The session cookie is `Secure`, so browsers don't store it over plain HTTP, and browsers only send
  `Sec-Fetch-Site` to secure origins. Browsers treat `http://localhost` as secure, except Safari, which needs `ng serve --ssl` in development. So that
  this fails clearly rather than signing in and bouncing straight back, the Backend refuses sign-ins over plain HTTP except from this machine.
- **The frontend and the API on one origin.** The cookie is `SameSite=Strict` and the CSRF checks require same-origin requests. In production the Backend
  serves the built frontend; in development the Angular dev server forwards `/api` to the Backend.
- **No CORS policy.** The custom-header check relies on the Backend refusing every cross-origin preflight. Allowing another origin with credentials would let
  that origin pass every CSRF check.
- **Forwarded headers behind a reverse proxy.** Without them, the Backend sees the proxy's IP for every client, so the per-client sign-in limit becomes
  one shared limit, the `Origin` fallback compares against the proxy's `Host`, and a proxy terminating TLS looks like plain HTTP, so sign-ins are refused.
  (The dev server's proxy rewrites `Host` too; requests still pass because browsers send `Sec-Fetch-Site`.)

## Credentials

The Owner's username and password come only from `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`, in development, E2E tests and production alike.
There's no user table, no setup page and no way to change them in the UI: with one Owner and credentials in `.env`, a user store would only be a copy to keep
in sync. If TOTP, passkeys or changing the password in the UI are added later, ASP.NET Core Identity and its tables become worth adopting then.

- The Backend refuses to start if either variable is missing or the password is shorter than 16 characters, like any other missing setting. The AppHost
  checks the same, so the error appears before anything else starts.
- The username is compared ignoring case; the password exactly. Both comparisons run in constant time on fixed-length hashes, and both always run, so the
  response time reveals neither which one was wrong nor the input lengths.
- Every failed sign-in gets the same `401` and message, including missing fields or no body at all, which is why `SignInRequest` and its fields are
  nullable instead of required.
- Sign-ins are logged with the client IP. The attempted username isn't logged, in case a password was typed into it.

## Session

Signing in sets a cookie session. A cookie, rather than a bearer token held by the frontend, keeps the credential out of reach of page scripts (`HttpOnly`)
and is sent on the live-updates WebSocket without putting a token in its URL.

| Setting | Value | Why |
| --- | --- | --- |
| Name | `__Host-lookout.session` | The prefix stops a sibling subdomain planting a same-named cookie that shadows it |
| `HttpOnly` | yes | Page scripts can't read it |
| `Secure` | always | Never sent over plain HTTP |
| `SameSite` | `Strict` | Not sent with requests started by other sites |
| Lifetime | 14 days, extended with use | One Owner, so no "remember me" choice: it would always be ticked |

The API answers `401` or `403` and never redirects to a login page; the frontend decides where to go.

**Credentials fingerprint.** The session carries a fingerprint of the credentials it signed in with, derived with PBKDF2 from the password, salted with
the upper-cased username. Every request compares it with the current credentials' fingerprint. So changing the username or password in `.env` and
restarting signs out every session, which is how the Owner recovers from a leaked password. Changing only the username's case doesn't, since the username is
compared ignoring case. The fingerprint is derived once per start, so it can afford to be slow, and it sits inside the encrypted cookie anyway.

**Signing out ends every session.** The session also carries a generation number, stored in the database and cached in memory. Signing out moves to the
next generation, so every existing cookie stops working, including copies of the one that signed out: deleting a cookie in the browser alone wouldn't stop a
stolen copy. With one Owner, "sign out" and "sign out everywhere" are the same thing. Only a request with a session can sign out; without one, signing out
just clears the caller's cookie, so anyone can't sign the Owner out.

**Data Protection keys** encrypt the cookie. They're kept in `LOOKOUT_DATABASE_DIR/keys`, so sessions survive restarts and are backed up with the database.
Deleting the keys signs everyone out. They're stored unencrypted, as anyone who can read them can also read the database and `.env`.

## Which endpoints are open

A fallback authorization policy requires the Owner on every endpoint that doesn't say otherwise, including the updates hub and requests that match no
endpoint. A new endpoint can't be left open by mistake: `EndpointProtectionTests` lists every routed endpoint and fails if one answers without a session
and isn't on its allow-list.

| Endpoint | Open because |
| --- | --- |
| `POST /api/auth/login` | It's how a session starts |
| `POST /api/auth/logout` | A client whose session expired can still clear its cookie; it's still CSRF-checked |
| The built frontend's files, and its `index.html` for every path outside `/api` | The page holds nothing private; every API call it makes needs a session |
| Development only: `/openapi/*`, `/api-docs` (Scalar) | So the API can be explored signed out; requests made from Scalar still need a session |
| Development only: `/health`, `/alive` | The AppHost polls them without signing in |

Routes under `/api` are left to routing, never the frontend's page: an unknown one answers `404` to the Owner and `401` to anyone else, and a known one called
with the wrong method answers `405`. `index.html` is sent with `Cache-Control: no-cache`, so a rebuilt frontend is picked up straight away, and the
content-hashed bundles are cached for good.

## The Runner

The Runner runs any script in the scripts directory that it's asked to, so asking it directly would skip signing in. Two things stop that:

- **The Runner must not be reachable from outside.** Only the Backend calls it. The AppHost runs it on this machine, and a deployment must keep its port off
  the LAN and the internet, just like browserless's and gluetun's. This is the real protection.
- **It requires a key.** The AppHost generates `LOOKOUT_RUNNER_API_KEY` on each start and gives it to the Backend and the Runner only. The Backend sends it
  in `X-API-Key`, and the Runner answers `401` to any request without it, compared in constant time. Like the Backend's sign-in, a fallback policy
  requires it on every endpoint that doesn't say otherwise, and `RunnerApiKeyTests` fails if a new endpoint answers without it. Both services refuse to
  start without a key of at least 32 characters, so the check can't be turned off by forgetting to set it. Outside the AppHost, give both the same key.

The key is defence in depth, for when something else on the network can reach the Runner's port. It doesn't help against a watch script: the Runner leaves
it out of the environment scripts run with, but a script runs as the Runner's user, so it can still find the key, for example in the Runner's own
environment. Keeping it from scripts is part of sandboxing them.

| Runner endpoint | Open because |
| --- | --- |
| Development only: `/health`, `/alive` | The AppHost polls them without the key |
| Development only: `/openapi/*`, `/api-docs` (Scalar) | So the API can be explored; requests made from Scalar still need the key |

To call the Runner from Scalar, copy the key from the `runner-api-key` parameter in the Aspire dashboard.

## The containers

browserless and gluetun's HTTP proxy would each let anyone who reaches them use Lookout's resources: browserless drives Chromium, including against the
internal network, and the proxy sends traffic out through the VPN. Like the Runner, they must not be reachable from outside, and each also requires a
secret that the AppHost generates on each start:

| Container | Secret | Who has it |
| --- | --- | --- |
| browserless | `browserless-token`, in the `token` query parameter of `LOOKOUT_BROWSERLESS_URL` | browserless and the Runner |
| gluetun's HTTP proxy | `gluetun-proxy-password`, for the username `lookout`, in `LOOKOUT_VPN_PROXY_URL` | gluetun, the Runner and the Backend; see below |
| gluetun's control server | `gluetun-api-key`, in `X-API-Key` | gluetun and the Backend |

The proxy URL holds its password, so the Runner hands it to the watch CLI in `LOOKOUT_WATCH_PROXY_URL`, not on the command line, which any user on the
machine can list. Only runs that use the VPN get it. Like the Runner key, it doesn't keep the secrets from a watch script: the browserless URL and, for VPN
runs, the proxy URL are in the script's environment, since the CLI needs them.

**Websites in VPN runs can be sent the proxy password.** Playwright gives Chromium the proxy's credentials as credentials for every site, so a page or
subresource that answers `401` with a Basic challenge during a VPN run is sent `lookout` and the password. It also makes Playwright intercept every request
in VPN runs and turn off the HTTP cache. We accept both: the password is only useful to something that can already reach the proxy, which stays off the LAN
and the internet, and the watches, and so the sites VPN runs visit, are the Owner's. Revisit this if Playwright starts telling proxy challenges from site
challenges, or if VPN runs become too slow; a small proxy in front of gluetun that adds the credentials itself would avoid both.

## CSRF

The browser attaches the session cookie automatically, so a page the Owner visits could try to make requests that use it. `SameSite=Strict` stops other
*sites*, but not other apps on the same site, such as `grafana.home.example.com` next to `lookout.home.example.com`, or another `localhost` port. Since a
forged `POST /api/Watches` could run code on the host, `CsrfProtection` adds two checks.

Every POST, PUT, PATCH and DELETE must:

1. **Carry `X-Lookout-Request`**, with any value. A plain HTML form can't add custom headers, and a cross-origin `fetch` that adds one triggers a CORS
   preflight, which the Backend refuses.
2. **Come from Lookout's own origin**: `Sec-Fetch-Site: same-origin`, or, from a browser without Fetch Metadata, an `Origin` matching the request's own.
   Browsers set both headers and pages can't change them. A request with neither is rejected: every browser sends one, and the browser is Lookout's only
   client.

**WebSocket handshakes** (an HTTP/1.1 `GET` asking to upgrade, or an HTTP/2 extended `CONNECT`, which Chrome uses) get the second check only, since
browsers can't add headers to them. They need it because WebSockets aren't covered by CORS: without it, a page on a sibling subdomain, which is sent the
`SameSite=Strict` cookie, could open the updates hub and listen in.

**Why not antiforgery tokens.** ASP.NET Core's antiforgery tokens are the usual defence, but they're aimed at server-rendered forms. With no CORS policy, they
add no protection over these two checks for a SPA calling a JSON API, and issuing and refreshing them in a SPA (after sign-in, for the hub's negotiate
request) is where token setups usually go wrong. Tokens would only add protection against a browser bug that skips the preflight, and the Fetch Metadata
check is an independent second line against that. The approach matches Go's `http.CrossOriginProtection` ([Filippo Valsorda's
explanation](https://words.filippo.io/csrf/)), and the [OWASP CSRF Prevention Cheat
Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html) lists both custom request headers and Fetch
Metadata as defences. ASP.NET Core 11 adds built-in Fetch Metadata checks, but only for form posts, not JSON endpoints.

If Lookout ever accepts API tokens, apply these checks only to requests authenticated by the cookie: browsers don't send tokens on their own, so those
requests can't be forged, and the checks would only block legitimate scripts.

## Sign-in rate limits

`POST /api/auth/login` allows 5 attempts a minute per client; nothing else is rate-limited. A client is an IPv4 address or an IPv6 /64 network, since one
IPv6 client usually controls a whole /64 and could otherwise use a fresh address for every attempt.

There's deliberately no limit across all clients, and no account lockout: with a single account, either would let anyone with a few addresses keep the Owner
from signing in for as long as they kept guessing wrong. Guessing spread across many addresses is left to the 16-character minimum, which makes it hopeless.

## Frontend

- `Session` mirrors what the Backend last said about the cookie, which page scripts can't read. A route guard sends signed-out visitors to
  `/login?returnUrl=…`.
- `returnUrl` is only followed if it's a path inside the app (`/…` but not `//…`), so a crafted link can't send the Owner to another site after signing in.
- An interceptor adds `X-Lookout-Request` to every API request and treats a `401` from anything but the auth endpoints as the session ending.
- **Live updates follow the session.** The connection starts on sign-in and stops on sign-out. The Backend closes it when the session expires, and refuses
  to reconnect once the session has ended. SignalR wraps the negotiate request's `401` in an error without the status code, so the connection uses its own
  HTTP client, `SessionAwareHttpClient`, which sees the `401` and ends the session in the frontend, which stops reconnecting.
- Signing out waits for the Backend: if it can't be reached, the frontend says so and stays signed in, since the cookie can't be cleared from a page script
  and would still work.

## Development

- The OpenAPI document declares `X-Lookout-Request` as a header security scheme, and Scalar fills it in on every request.
- Sign in by calling `POST /api/auth/login` from Scalar, or in the frontend: cookies ignore the port, so a session started on `localhost:4200` also works
  on the Backend's port.

## Not built yet

- TOTP, passkeys, and creating or changing credentials in the UI.
- API tokens for callers other than the browser (see the CSRF note above).
- Several users, which first needs watch scripts sandboxed ([ADR 0001](adr/0001-single-owner.md)).
- Production deployment: container images, TLS termination and forwarded headers.
