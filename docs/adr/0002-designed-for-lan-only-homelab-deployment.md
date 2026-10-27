# Designed for LAN-only homelab deployment

Lookout targets homelabs. It is designed to be reachable only from the local network; reaching it from outside requires the user's own VPN (e.g. Tailscale or WireGuard). It is not hardened for direct internet exposure. It requires a single local Account (cookie authentication) to protect Script upload and execution. Scripts are trusted code: users must review every one they upload, and Lookout does not try to contain a hostile Script ([ADR-0007](0007-scripts-are-trusted-and-direct-runs-reach-the-lan.md)). The Script sandbox keeps Scripts separate from Lookout's database and secrets ([ADR-0003](0003-scripts-run-in-an-offline-runner-container.md)); it is not a defence against an unreviewed Script or a stolen login.

## Consequences

Supporting direct internet exposure later would require at least:

- HTTPS terminated by a reverse proxy, with `Secure`, `HttpOnly` and `SameSite` cookies and HSTS.
- Login limiting that tells clients apart behind a reverse proxy. Since [#48](https://github.com/chasepie/lookout/issues/48), Lookout refuses logins from a client address for the rest of a 5-minute window after 5 failed logins from it, but it doesn't know the proxy's address, so it can't trust `X-Forwarded-For`, and behind a proxy every client shares one limit: someone failing logins on purpose can keep the owner locked out, 5 minutes at a time. That's accepted on a LAN, not on the internet.
- Stronger authentication: passkeys, TOTP, or OIDC via an existing identity provider.
- Revisit the trusted-Script decision and review what containing a hostile Script would require.
- Security headers (CSP, frame-ancestors) on the Angular app.
