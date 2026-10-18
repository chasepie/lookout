# Designed for LAN-only homelab deployment

Lookout targets homelabs. It is designed to be reachable only from the local network; reaching it from outside requires the user's own VPN (e.g. Tailscale or WireGuard). It is not hardened for direct internet exposure. As defence in depth, it still requires a single local account (cookie authentication), and Scripts run isolated from the backend's data and secrets, because a stolen login would otherwise turn Script upload into code execution on the host.

## Consequences

Supporting direct internet exposure later would require at least:

- HTTPS terminated by a reverse proxy, with `Secure`, `HttpOnly` and `SameSite` cookies and HSTS.
- Login rate limiting and account lockout.
- Stronger authentication: passkeys, TOTP, or OIDC via an existing identity provider.
- A review of the Script sandbox against a hostile author, rather than "a script I didn't fully read".
- Security headers (CSP, frame-ancestors) on the Angular app.
