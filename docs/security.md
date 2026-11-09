# Security

How Lookout decides who can use it, and why it works the way it does. The code documents what each piece does; this
document explains the reasoning and the constraints that must stay true as Lookout changes. Terms such as **Owner** are
defined in [`GLOSSARY.md`](../GLOSSARY.md).

## Threat model

Uploading a watch script is running code: the Runner executes it with Node. [Watch scripts](#watch-scripts) limits what
that code can reach, but it can still drive the browser anywhere and use the network. Everything below exists to make
sure only the Owner can use the API, and only the Backend can ask the Runner to run a script.

Lookout runs on a LAN or tailnet today, but it's designed as if it were exposed to the internet. It defends against:

- **Anyone who can reach the Backend**, who must sign in first.
- **Malicious pages in the Owner's browser**: other sites, and, more importantly, other apps on the same site, such as a
  sibling subdomain in a homelab or another dev server on `localhost`. These are the cross-site request forgery (CSRF)
  cases.
- **Password guessing**, by rate limiting.

It doesn't defend against a compromised Owner browser (XSS in Lookout itself defeats every check here) or a compromised
host. Against a malicious watch script, it only limits the damage: the script can't read Lookout's secrets or files, but
it can reach the network and anything the browser can, which is why there is only one Owner ([ADR
0001](adr/0001-single-owner.md)).

## Requirements on how Lookout is served

These aren't visible in any one file, and breaking them breaks signing in or weakens the checks:

- **HTTPS everywhere except `localhost`.** The session cookie is `Secure`, so browsers don't store it over plain HTTP,
  and browsers only send `Sec-Fetch-Site` to secure origins. Browsers treat `http://localhost` as secure, except Safari,
  which needs `ng serve --ssl` in development. So that this fails clearly rather than signing in and bouncing straight
  back, the Backend refuses sign-ins over plain HTTP except from this machine.
- **The frontend and the API on one origin.** The cookie is `SameSite=Strict` and the CSRF checks require same-origin
  requests. In production the Backend serves the built frontend; in development the Angular dev server forwards `/api`
  to the Backend.
- **No CORS policy.** The custom-header check relies on the Backend refusing every cross-origin preflight. Allowing
  another origin with credentials would let that origin pass every CSRF check.
- **Forwarded headers behind a reverse proxy.** Without them, the Backend sees the proxy's IP for every client, so the
  per-client sign-in limit becomes one shared limit, and a proxy terminating TLS looks like plain HTTP, so sign-ins are
  refused. The Backend takes the client's address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto` (one hop),
  but only from the addresses in `LOOKOUT_TRUSTED_PROXIES`: anyone else could set them to dodge the sign-in limit or
  pass for HTTPS. With none listed, it ignores both headers. ASP.NET Core would otherwise trust every source when no
  proxy is listed, so `ReverseProxy` turns forwarding off instead. `Host` isn't taken from the proxy, so it must pass
  the browser's through. (The dev server's proxy rewrites `Host`; requests still pass because browsers send
  `Sec-Fetch-Site`.)
- **One hostname.** `AllowedHosts` is the hostname Lookout is served on, `localhost` unless set (`LOOKOUT_HOST` in
  production), and requests for any other get `400`. The Backend refuses to start with it empty, which ASP.NET Core
  would take as "every host".

## Credentials

The Owner's username and password come only from `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`, in development,
E2E tests and production alike. There's no user table, no setup page and no way to change them in the UI: with one Owner
and credentials in `.env`, a user store would only be a copy to keep in sync. If TOTP, passkeys or changing the password
in the UI are added later, ASP.NET Core Identity and its tables become worth adopting then.

- The Backend refuses to start if either variable is missing or the password is shorter than 16 characters, like any
  other missing setting. The AppHost checks the same, so the error appears before anything else starts.
- The username is compared ignoring case; the password exactly. Both comparisons run in constant time on fixed-length
  hashes, and both always run, so the response time reveals neither which one was wrong nor the input lengths.
- Every failed sign-in gets the same `401` and message, including missing fields or no body at all, which is why
  `SignInRequest` and its fields are nullable instead of required.
- Sign-ins are logged with the client IP. The attempted username isn't logged, in case a password was typed into it.

## Session

Signing in sets a cookie session. A cookie, rather than a bearer token held by the frontend, keeps the credential out of
reach of page scripts (`HttpOnly`) and is sent on the live-updates WebSocket without putting a token in its URL.

| Setting | Value | Why |
| --- | --- | --- |
| Name | `__Host-lookout.session` | The prefix stops a sibling subdomain planting a same-named cookie that shadows it |
| `HttpOnly` | yes | Page scripts can't read it |
| `Secure` | always | Never sent over plain HTTP |
| `SameSite` | `Strict` | Not sent with requests started by other sites |
| Lifetime | 14 days, extended with use | One Owner, so no "remember me" choice: it would always be ticked |

The API answers `401` or `403` and never redirects to a login page; the frontend decides where to go.

**Credentials fingerprint.** The session carries a fingerprint of the credentials it signed in with, derived with PBKDF2
from the password, salted with the upper-cased username. Every request compares it with the current credentials'
fingerprint. So changing the username or password in `.env` and restarting signs out every session, which is how the
Owner recovers from a leaked password. Changing only the username's case doesn't, since the username is compared
ignoring case. The fingerprint is derived once per start, so it can afford to be slow, and it sits inside the encrypted
cookie anyway.

**Signing out ends every session.** The session also carries a generation number, stored in the database and cached in
memory. Signing out moves to the next generation, so every existing cookie stops working, including copies of the one
that signed out: deleting a cookie in the browser alone wouldn't stop a stolen copy. With one Owner, "sign out" and
"sign out everywhere" are the same thing. Only a request with a session can sign out; without one, signing out just
clears the caller's cookie, so anyone can't sign the Owner out.

**Data Protection keys** encrypt the cookie. They're kept in `LOOKOUT_DATABASE_DIR/keys`, so sessions survive restarts
and are backed up with the database. Deleting the keys signs everyone out. They're stored unencrypted, as anyone who can
read them can also read the database and `.env`.

## Which endpoints are open

A fallback authorization policy requires the Owner on every endpoint that doesn't say otherwise, including the updates
hub and requests that match no endpoint. A new endpoint can't be left open by mistake: `EndpointProtectionTests` lists
every routed endpoint and fails if one answers without a session and isn't on its allow-list.

| Endpoint | Open because |
| --- | --- |
| `POST /api/auth/login` | It's how a session starts |
| `POST /api/auth/logout` | A client whose session expired can still clear its cookie; it's still CSRF-checked |
| The built frontend's files, and its `index.html` for every path outside `/api` | The page holds nothing private; every API call it makes needs a session |
| Development only: `/openapi/*`, `/api-docs` (Scalar) | So the API can be explored signed out; requests made from Scalar still need a session |
| Development only: `/health`, `/alive` | The AppHost polls them without signing in |

Routes under `/api` are left to routing, never the frontend's page: an unknown one answers `404` to the Owner and `401`
to anyone else, and a known one called with the wrong method answers `405`. `index.html` is sent with
`Cache-Control: no-cache`, so a rebuilt frontend is picked up straight away, and the content-hashed bundles are cached
for good.

## The Runner

The Runner runs any script in the scripts directory that it's asked to, so asking it directly would skip signing in. Two
things stop that:

- **The Runner must not be reachable from outside.** Only the Backend calls it. In development the AppHost runs it on
  this machine; in production it's only on internal networks, shared with the Backend and browserless (see [Production
  deployment](#production-deployment)). This is the real protection.
- **It requires a key.** The AppHost generates `LOOKOUT_RUNNER_API_KEY` on each start and gives it to the Backend and
  the Runner only. The Backend sends it in `X-API-Key`, and the Runner answers `401` to any request without it, compared
  in constant time. Like the Backend's sign-in, a fallback policy requires it on every endpoint that doesn't say
  otherwise, and `RunnerApiKeyTests` fails if a new endpoint answers without it. Both services refuse to start without a
  key of at least 32 characters, so the check can't be turned off by forgetting to set it. Outside the AppHost, give
  both the same key: in production, `LOOKOUT_RUNNER_API_KEY` in `.env`.

The key is defence in depth, for when something else on the network can reach the Runner's port. Scripts don't get it:
see [Watch scripts](#watch-scripts).

The Backend calls the Runner over plain HTTP, since the Runner's container has no certificate of its own. The hop stays
on this machine in development, and on the internal `runner` network in production.

| Runner endpoint | Open because |
| --- | --- |
| Development only: `/health`, `/alive` | The AppHost polls them without the key |
| Development only: `/openapi/*`, `/api-docs` (Scalar) | So the API can be explored; requests made from Scalar still need the key |

To call the Runner from Scalar, copy the key from the `runner-api-key` parameter in the Aspire dashboard.

## Watch scripts

A watch script is code, so it runs inside two limits, for when the sign-in is bypassed or a script depends on a
compromised npm package.

**Node's permission model.** `ScriptRunner` starts the watch CLI with `--permission`, so a script may only read itself
(not the other watches' scripts), the `node_modules` the CLI imports from and the SDK's own directory; write only the
file the CLI puts its result in; and start no child processes or worker threads. It starts with a clean environment:
only `FORCE_COLOR`, `BROWSERLESS_URL`, `LOOKOUT_WATCH_PROXY_URL` for VPN runs, and `TZ` and `SystemRoot` when the Runner
has them. So the Runner key, and anything else in the Runner's environment, never reach a script. `ScriptSandboxTests`
runs scripts that try each of these and checks they're refused. Node calls its model a seat belt, not a sandbox, so it
isn't the only limit:

- Every path Node is given is resolved through links first: the permission model only matches real paths, and the temp
  directory is a link on macOS. A script that is itself a link is refused, since it would carry its read permission to
  wherever it points.
- Outside the container, as in the Runner's tests, `node_modules` is the repo's, and npm links every workspace into it.
  Node follows those links, so a script run there can read the `src/Runner`, `src/frontend` and `examples` workspaces.
  In the container, those links point nowhere.
- The CLI needs the network to reach browserless, and Node can't limit it to one host, so scripts get `--allow-net`.
- A local browser needs child processes, so the Runner refuses to start without `BROWSERLESS_URL`.

**The Runner's container.** The Runner runs from `src/Runner/Dockerfile` as the image's non-root user, with no Linux
capabilities, `no-new-privileges`, `tini` as PID 1 (see [Production deployment](#production-deployment)), and the
scripts mounted read-only at `/app/scripts`. The container has no `.env`, database, Data Protection keys or repo. In
production its root filesystem is read-only too, with a tmpfs for `/tmp`, and it has a memory limit, since outside V8's
heap nothing else stops a script taking the host's memory. In development the root filesystem stays writable: Aspire
copies the development certificate into it, which the Runner's telemetry needs, and `--read-only` refuses that. The
image's files belong to root, so the Runner's user can only write `/tmp` and its home directory anyway. The Runner's
user is uid 1654, so on a Linux host the scripts directory must be readable by it, for example with the default `umask`;
Docker Desktop on macOS hides this.

**What they don't limit.** A script drives Chromium in browserless, which reaches the internet and the LAN, so it can
still send out what it can read, which is its own source, and reach services on the network. In production, that's its
only way out: the Runner's networks lead only to the Backend and browserless, so a script's own Node code reaches
nothing else. In development, Node's network is open too, since limiting it needs the Backend in a container.

## The containers

browserless and gluetun's HTTP proxy would each let anyone who reaches them use Lookout's resources: browserless drives
Chromium, including against the internal network, and the proxy sends traffic out through the VPN. Like the Runner, they
must not be reachable from outside, and each also requires a secret that the AppHost generates on each start:

| Container | Secret | Who has it |
| --- | --- | --- |
| browserless | `browserless-token`, the Runner's `BROWSERLESS_TOKEN`, sent as the `token` query parameter | browserless and the Runner |
| gluetun's HTTP proxy | `gluetun-proxy-password`, `GLUETUN_PROXY_PASSWORD`, for the username `lookout` | gluetun, the Runner and the Backend; see below |
| gluetun's control server | `gluetun-api-key`, in `X-API-Key` | gluetun and the Backend |

Production takes them from the deployment's `.env` instead: see [Production deployment](#production-deployment). The
token and the password are settings of their own rather than part of their URLs, so each is set once beside the
container's own. A script's environment still holds them in URLs, and Playwright prints the browserless URL when it
can't connect, so the Runner masks both secrets in a script's output before logging it or keeping it as the run's error.
The Runner refuses to start without a browserless token, since browserless without one accepts anyone, and the Runner
having none means it was started without one. Likewise, the Backend and the Runner refuse a proxy URL without a
password, and the Backend gluetun without its key.

gluetun's control-server key may only use the routes the Backend does (reading and changing the VPN's settings, and the
public IP), through a role in gluetun's auth file; gluetun refuses every other route. Its proxy runs in stealth mode,
without which it adds headers to plain-HTTP requests that give away browserless's address.

The proxy URL holds its password, so the Runner hands it to the watch CLI in `LOOKOUT_WATCH_PROXY_URL`, not on the
command line, which any user on the machine can list. Only runs that use the VPN get it. Unlike the Runner key, these
two secrets aren't kept from scripts: the browserless URL and, for VPN runs, the proxy URL are in the script's
environment, since the CLI needs them.

**Websites in VPN runs aren't sent the proxy password.** Playwright answers only the proxy's own challenges with the
proxy's credentials, and a site's `401` Basic challenge fails the request as it does outside VPN runs. Playwright before
1.65 answers sites' challenges with them too
([microsoft/playwright#43326](https://github.com/microsoft/playwright/issues/43326)), so the SDK's and the Runner's
Playwright stay at 1.65 or later. The Playwright that answers these challenges is the one inside the browserless image,
which serves each client the Playwright version the client has, so the image must support 1.65 too (browserless v2.60.0
or later, in `src/AppHost/Images.cs`). The image also serves older clients, so an older SDK would send sites the
password without anything failing. Proxy credentials make Playwright intercept every request in VPN runs and turn off
the HTTP cache, which costs speed but not security. If VPN runs become too slow, a small proxy in front of gluetun that
adds the credentials itself would avoid the interception.

## Response headers

Every response, API and frontend alike, carries:

| Header | Value | Why |
| --- | --- | --- |
| `Content-Security-Policy` | `default-src 'self'` with the exceptions below | Injected markup can't load or send anything elsewhere |
| `X-Content-Type-Options` | `nosniff` | A response is only ever what its `Content-Type` says |
| `Referrer-Policy` | `no-referrer` | Following a link out of Lookout, such as to a watched page, doesn't reveal its URLs |
| `Strict-Transport-Security` | `max-age=31536000`, outside Development | Browsers use HTTPS from then on |

The HSTS header isn't `includeSubDomains`, since sibling subdomains may be other apps on plain HTTP, and isn't
preloaded. The CSP's `frame-ancestors 'none'` stops other sites framing Lookout to trick clicks. It allows
`'unsafe-inline'` for styles only, since Angular adds component styles as `<style>` elements, and `data:` images, which
is how screenshots are shown. It allows no inline script: the production build doesn't inline critical CSS
(`inlineCritical: false` in `angular.json`), which would add one. In Development, Scalar's `/api-docs` gets no CSP,
since it loads its scripts from a CDN.

Script uploads are limited to 128 KiB a request (`[RequestSizeLimit]`), twice the largest script, so an oversized form
is cut off as it arrives rather than read whole before the 64 KiB check. Requests over it get `400`, like other invalid
forms.

## Production deployment

`aspire publish` writes the production deployment, `deploy/docker-compose.yaml`, from `src/AppHost/Production.cs`;
`deploy/README.md` says how to run it. Every service is a container, which development can't do without losing the
Backend's debugger, so production can limit what each one reaches. Why the file is generated and committed, why the
Backend reaches gluetun through relays, and why the secrets are in `.env` are ADRs
[0002](adr/0002-generated-and-committed-compose-file.md), [0003](adr/0003-socat-relays-to-gluetun.md) and
[0004](adr/0004-production-secrets-in-env.md).

**One network per pair of services that talk:**

| Network | Services | Leads out |
| --- | --- | --- |
| `runner` | Backend, Runner | no |
| `browsers` | Runner, browserless | no |
| `vpn-proxy` | browserless, gluetun | no |
| `gluetun-relay` | gluetun, the two relays | no |
| `vpn-control` | the two relays, Backend | no |
| `egress` | browserless, gluetun | yes |
| `edge` | Backend, for its published port | yes |

So the Runner reaches only the Backend and browserless. Chromium, which loads whatever pages scripts visit, shares no
network with the Backend. gluetun's proxy forwards to every network gluetun is on, so gluetun shares none with the
Backend either: the Backend reaches its control server and its proxy (for the VPN probe) through two `socat` relays,
which a page loaded through the proxy can reach, but which lead only back to gluetun. Chromium can reach the Runner,
which refuses requests without its key. A script in a VPN run gets the proxy's URL and password, so it can send traffic
out through gluetun, just as it can through Chromium.

The Backend's port is published on `127.0.0.1` unless `LOOKOUT_BIND_ADDRESS` says otherwise, for a reverse proxy that
terminates TLS. Docker only publishes ports from containers on a network that leads out, so the Backend can reach the
internet, though it has no reason to. The `edge` network has a fixed subnet (`LOOKOUT_EDGE_SUBNET`), so its gateway,
which connections to the published port come from and which a reverse proxy on the same machine is trusted as, stays the
same when the network is re-created. That trusts every process on the host that connects to the port, not just the
proxy; a proxy on another machine is trusted by its own address instead. The Compose project is named `lookout`, so the
volumes are `lookout_*` wherever the file is.

**Containers.** The Backend, the Runner and the relays run with a read-only root filesystem (a tmpfs for `/tmp`), no
Linux capabilities and `no-new-privileges`, and the Backend and the Runner as uid 1654. Both images start with `tini` as
PID 1: as PID 1 itself, .NET ignores the `SIGABRT` its own crash raises, since the kernel drops signals PID 1 has no
handler for, and spins instead of exiting, so a service that failed to start would look like it was running. There's no
Aspire dashboard, which would show every service's environment.

**Settings and secrets** are Compose variables in the deployment's `.env`, which it keeps unreadable to other users, and
reach each container in its environment, only the ones it needs. gluetun's control-server key also goes into its auth
file, which Compose writes from `.env`. Compose's secret files were the alternative, keeping secrets out of the
containers' environments and `docker inspect`, but browserless only reads its token from its environment, so some
secrets would have been in `.env` anyway, and one place is simpler than two. `docker inspect` shows them only to those
who can already run containers on the host. Scripts get only the browserless token and, in VPN runs, the proxy password:
the Runner starts each with a clean environment ([Watch scripts](#watch-scripts)).

Optional settings have defaults in the Compose file, so a setting left empty can't publish the port on every address.
The required ones have none, and the service that needs one refuses to start without it. Nothing in the generated files
comes from the publisher's machine, and `ProductionComposeTests` checks the networks, the containers and which secrets
reach which service.

## CSRF

The browser attaches the session cookie automatically, so a page the Owner visits could try to make requests that use
it. `SameSite=Strict` stops other *sites*, but not other apps on the same site, such as `grafana.home.example.com` next
to `lookout.home.example.com`, or another `localhost` port. Since a forged `POST /api/Watches` could run code on the
host, `CsrfProtection` adds two checks.

Every POST, PUT, PATCH and DELETE must:

1. **Carry `X-Lookout-Request`**, with any value. A plain HTML form can't add custom headers, and a cross-origin `fetch`
   that adds one triggers a CORS preflight, which the Backend refuses.
2. **Come from Lookout's own origin**: `Sec-Fetch-Site: same-origin`, or, from a browser without Fetch Metadata, an
   `Origin` matching the request's own. Browsers set both headers and pages can't change them. A request with neither is
   rejected: every browser sends one, and the browser is Lookout's only client.

**WebSocket handshakes** (an HTTP/1.1 `GET` asking to upgrade, or an HTTP/2 extended `CONNECT`, which Chrome uses) get
the second check only, since browsers can't add headers to them. They need it because WebSockets aren't covered by CORS:
without it, a page on a sibling subdomain, which is sent the `SameSite=Strict` cookie, could open the updates hub and
listen in.

**Why not antiforgery tokens.** ASP.NET Core's antiforgery tokens are the usual defence, but they're aimed at
server-rendered forms. With no CORS policy, they add no protection over these two checks for a SPA calling a JSON API,
and issuing and refreshing them in a SPA (after sign-in, for the hub's negotiate request) is where token setups usually
go wrong. Tokens would only add protection against a browser bug that skips the preflight, and the Fetch Metadata check
is an independent second line against that. The approach matches Go's `http.CrossOriginProtection` ([Filippo Valsorda's
explanation](https://words.filippo.io/csrf/)), and the [OWASP CSRF Prevention Cheat
Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html) lists both
custom request headers and Fetch Metadata as defences. ASP.NET Core 11 adds built-in Fetch Metadata checks, but only for
form posts, not JSON endpoints.

If Lookout ever accepts API tokens, apply these checks only to requests authenticated by the cookie: browsers don't send
tokens on their own, so those requests can't be forged, and the checks would only block legitimate scripts.

## Sign-in rate limits

`POST /api/auth/login` allows 5 attempts a minute per client; nothing else is rate-limited. A client is an IPv4 address
or an IPv6 /64 network, since one IPv6 client usually controls a whole /64 and could otherwise use a fresh address for
every attempt.

There's deliberately no limit across all clients, and no account lockout: with a single account, either would let anyone
with a few addresses keep the Owner from signing in for as long as they kept guessing wrong. Guessing spread across many
addresses is left to the 16-character minimum, which makes it hopeless.

## Frontend

- `Session` mirrors what the Backend last said about the cookie, which page scripts can't read. A route guard sends
  signed-out visitors to `/login?returnUrl=…`.
- `returnUrl` is only followed if it's a path inside the app (`/…` but not `//…`), so a crafted link can't send the
  Owner to another site after signing in.
- An interceptor adds `X-Lookout-Request` to every API request and treats a `401` from anything but the auth endpoints
  as the session ending.
- **Live updates follow the session.** The connection starts on sign-in and stops on sign-out. The Backend closes it
  when the session expires, and refuses to reconnect once the session has ended. SignalR wraps the negotiate request's
  `401` in an error without the status code, so the connection uses its own HTTP client, `SessionAwareHttpClient`, which
  sees the `401` and ends the session in the frontend, which stops reconnecting.
- Signing out waits for the Backend: if it can't be reached, the frontend says so and stays signed in, since the cookie
  can't be cleared from a page script and would still work.

## Development

- The OpenAPI document declares `X-Lookout-Request` as a header security scheme, and Scalar fills it in on every
  request.
- Sign in by calling `POST /api/auth/login` from Scalar, or in the frontend: cookies ignore the port, so a session
  started on `localhost:4200` also works on the Backend's port.

## Not built yet

- TOTP, passkeys, and creating or changing credentials in the UI.
- API tokens for callers other than the browser (see the CSRF note above).
- Several users, which needs their watch scripts isolated from each other, not only from Lookout ([ADR
  0001](adr/0001-single-owner.md)).
- A limit on the Runner's processes (`pids_limit`), which Aspire's Compose publisher can't write yet. Node's permission
  model already stops scripts starting processes.
- TLS inside Lookout: production expects a reverse proxy in front of it to terminate TLS.
