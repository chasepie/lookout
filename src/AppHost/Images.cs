namespace ChasePie.Lookout.AppHost;

// Every image and tag, so a version bump changes only this file
public partial class Program
{
    // Lookout's own images in production, which CD (.github/workflows/cd.yml) pushes. Their tag is a deployment setting,
    // lookout-image-tag. Development builds the Runner from its Dockerfile and runs the Backend as a project instead.
    private const string BackendImage = "ghcr.io/chasepie/lookout-backend";
    private const string RunnerImage = "ghcr.io/chasepie/lookout-runner";

    // The relays production puts between the Backend and gluetun (see AddRelay): socat forwards one TCP port to another
    private const string AlpineSocatImage = "alpine/socat";
    private const string AlpineSocatTag = "1.8.0.3";

    // Chromium that watch scripts run on instead of a locally launched browser. The SDK connects with Playwright, so the
    // image must support its Playwright version: v2.60.0 supports 1.61 to 1.65. Keep the SDK on 1.65 or later, the first
    // that doesn't send websites the VPN proxy's password (docs/security.md), and so the image on v2.60.0 or later.
    private const string BrowserlessImage = "ghcr.io/browserless/chromium";
    private const string BrowserlessTag = "v2.60.0";

    // The VPN client, with an HTTP proxy that watches with UseVpn send their browser traffic through, and a control
    // server the Backend switches regions with. It must require a key on the control server, which v3.40 started to.
    private const string GluetunImage = "qmcgaw/gluetun";
    private const string GluetunTag = "v3.41.3";
}
