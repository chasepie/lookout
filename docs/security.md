# Security

How Lookout decides who can use it, and why it works the way it does. The code documents what each piece does; this document explains the reasoning and the
constraints that must stay true as Lookout changes. Terms such as **Owner** are defined in [`GLOSSARY.md`](../GLOSSARY.md).

## Threat model

Uploading a watch script is the same as running code on the Runner's host: the Runner executes it with Node. So anyone who can use the API can read the
`.env` file, the database and anything else the Runner's user can reach. Everything below exists to make sure only the Owner can use the API.

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
  `Sec-Fetch-Site` to secure origins. Browsers treat `http://localhost` as secure, except Safari, which needs `ng serve --ssl` in development.
- **The frontend and the API on one origin.** The cookie is `SameSite=Strict` and the CSRF checks require same-origin requests. In production the Backend
  serves the built frontend; in development the Angular dev server forwards `/api` to the Backend.
- **No CORS policy.** The custom-header check relies on the Backend refusing every cross-origin preflight. Allowing another origin with credentials would let
  that origin pass every CSRF check.
- **Forwarded headers behind a reverse proxy.** Without them, the Backend sees the proxy's IP for every client, so the per-IP sign-in limit becomes one
  shared limit, and the `Origin` fallback compares against the proxy's `Host`. (The dev server's proxy rewrites `Host` too; requests still pass because
  browsers send `Sec-Fetch-Site`.)

## Credentials

The Owner's username and password come only from `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`, in development, E2E tests and production alike.
There's no user table, no setup page and no way to change them in the UI: with one Owner and credentials in `.env`, a user store would only be a copy to keep
in sync. If TOTP, passkeys or changing the password in the UI are added later, ASP.NET Core Identity and its tables become worth adopting then.

- The Backend refuses to start if either variable is missing or the password is shorter than 16 characters, like any other missing setting. The AppHost
  also refuses to start without them, so the error appears before anything else starts.
- The username is compared ignoring case; the password exactly. Both comparisons run in constant time on fixed-length hashes, and both always run, so the
  response time reveals neither which one was wrong nor the input lengths.
- Every failed sign-in gets the same `401` and message, including missing fields, which is why `SignInRequest`'s fields are nullable instead of required.
- Sign-ins are logged with the client IP. The attempted username isn't logged, in case a password was typed into it.

## Session

Signing in sets a cookie session. A cookie, rather than a bearer token held by the frontend, keeps the credential out of reach of page scripts (`HttpOnly`)
and is sent on the live-updates WebSocket without putting a token in its URL.

| Setting | Value | Why |
| --- | --- | --- |
| Name | `lookout.session` | |
| `HttpOnly` | yes | Page scripts can't read it |
| `Secure` | always | Never sent over plain HTTP |
| `SameSite` | `Strict` | Not sent with requests started by other sites |
| Lifetime | 14 days, extended with use | One Owner, so no "remember me" choice: it would always be ticked |

The API answers `401` or `403` and never redirects to a login page; the frontend decides where to go.

**Credentials fingerprint.** The session carries a fingerprint of the credentials it signed in with, derived with PBKDF2 from the password, salted with
the upper-cased username. Every request compares it with the current credentials' fingerprint. So changing the username or password in `.env` and
restarting signs out every session, which is how the Owner recovers from a leaked password. Changing only the username's case doesn't, since the username is
compared ignoring case. The fingerprint is derived once per start, so it can afford to be slow, and it sits inside the encrypted cookie anyway.

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
| The built frontend's files and `index.html` fallback | The page holds nothing private; every API call it makes needs a session |
| Development only: `/openapi/*`, `/api-docs` (Scalar) | So the API can be explored signed out; requests made from Scalar still need a session |
| Development only: `/health`, `/alive` | The AppHost polls them without signing in |

Unknown routes under `/api` answer `404` to the Owner instead of the frontend's page, and `401` to anyone else.

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

`POST /api/auth/login` allows 5 attempts a minute per client IP and 20 a minute in total; nothing else is rate-limited. The total limit caps guessing spread
across many IPs. There's no account lockout: with a single account, anyone could lock the Owner out by guessing wrong on purpose. The worst a flood of
attempts can do here is make the Owner wait a minute. A blocked IP's attempts don't count against the total.

## Frontend

- `Session` mirrors what the Backend last said about the cookie, which page scripts can't read. A route guard sends signed-out visitors to
  `/login?returnUrl=…`.
- `returnUrl` is only followed if it's a path inside the app (`/…` but not `//…`), so a crafted link can't send the Owner to another site after signing in.
- An interceptor adds `X-Lookout-Request` to every API request and treats a `401` from anything but the auth endpoints as the session ending.
- **Live updates follow the session.** The connection starts on sign-in and stops on sign-out. The Backend closes it when the session expires and refuses
  it after the credentials change, but SignalR doesn't say why a reconnect failed: it wraps the negotiate request's `401` in an error without the status
  code. So the client asks `/api/auth/me` before each reconnect attempt. Asking only once isn't enough: right after a restart, the question can reach a
  Backend that is still shutting down and still accepts the old session.

## Development

- The OpenAPI document declares `X-Lookout-Request` as a header security scheme, and Scalar fills it in on every request.
- Sign in by calling `POST /api/auth/login` from Scalar, or in the frontend: cookies ignore the port, so a session started on `localhost:4200` also works
  on the Backend's port.

## Not built yet

- TOTP, passkeys, and creating or changing credentials in the UI.
- API tokens for callers other than the browser (see the CSRF note above).
- Several users, which first needs watch scripts sandboxed ([ADR 0001](adr/0001-single-owner.md)).
- Production deployment: container images, TLS termination and forwarded headers.
