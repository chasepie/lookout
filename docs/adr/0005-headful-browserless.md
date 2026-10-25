# Headful Browserless

The runner asks Browserless for a headful Chromium (`headless: false` in each connection's launch options), drawn on the Xvfb display Browserless's image starts. Headless Chromium gives itself away to bot detection, and headful avoids that without the runner disguising anything. This reverses the prototype's recommendation ([docs/research/browser-and-vpn-prototype.md](../research/browser-and-vpn-prototype.md)), which was to stay headless and rewrite `HeadlessChrome` to `Chrome` in each context's user agent.

## Considered Options

- **Headless with a user-agent override.** Rejected, though it passed browserscan and reached dedicated workers too. The override has to rebuild the user agent from the browser's version on every connection, and it can't hide other tells. Headless reports its window's outer size equal to its viewport (1280x720 for both), where headful reports a real window around it (1288x805 around 1280x720).

## Consequences

- A Script's page can't call `page.pdf()`, which Playwright supports only in headless Chromium.
- Headful draws every frame to Xvfb. In a check with two browsers at once on Docker Desktop (2026-10-25), the Browserless container used about 10% CPU on a mostly static page that settled at about 3% headless, and about the same on an animated page (20–60% in both modes). Memory was the same in both.
- Every concurrent browser shares one Xvfb display, so Xvfb failing fails every Run in progress.
- The other fingerprint gaps the prototype found (`Linux x86_64`, a SwiftShader WebGL renderer, the `America/Los_Angeles` time zone) are the same either way.
