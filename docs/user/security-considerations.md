# Security considerations

Scripts are trusted code. Review every Script before uploading it, including each new Script version: choosing a file can start a Test run immediately. A Script can drive a browser, read pages and send information to other sites. Lookout does not try to make a hostile Script safe to run. Only upload code you understand and trust.

## What Scripts and pages can reach

A Direct Watch's browser can reach your local network (LAN), like another browser on it. That includes a Script opening a local address and a public page making requests or redirecting to one on its own. Reviewing the Script does not control everything the pages it visits will do.

| Access from the browser | Direct Watches | VPN Watches |
| --- | --- | --- |
| Public sites | From your home internet address | Through the VPN |
| Other devices on your LAN, such as a router | Can reach them | Blocked by the VPN's firewall |
| Services published by Docker on the machine running Lookout, including Lookout itself | Can reach them | Can still reach them |

Pages in Direct Watches can also reach Browserless, the service running their browsers. Its control API currently has no access key, so do not treat those controls as protected from pages. The probe confirmed a response, but did not test effects on other Runs. Requiring an access key is tracked in [#49](https://github.com/chasepie/lookout/issues/49).

These rules also apply to Test runs. A VPN Watch whose VPN is unavailable does not fall back to your home internet connection.

Part of a Script runs outside the browser, using Node. In the Docker deployment, that code has no direct internet access. It may still reach services published by Docker on the same machine, whether the Watch uses the VPN or not. This is expected on Linux Docker Engine from Docker's documentation; it has not been verified there. The Docker Desktop probes tested browser access, not this Node access, which has not been probed there either. Assume it may be possible on Docker Desktop too. A VPN Watch is not a way to protect other services on the Docker host from a Script.

## What stays protected

In the supplied Docker deployment, Scripts run in a separate container without Lookout's database, private settings or files containing secrets. Scripts are not given the access key Lookout uses to request Runs, or your VPN-provider login, such as your PIA user name and password. If you configure a separate HTTP proxy login for the browser's connection to the VPN, Scripts do receive that user name and password and can read them. This is accepted because Scripts are trusted. These protections keep Lookout's data and secrets separate from Scripts; they are not a guarantee against hostile code. See [how Scripts run](../adr/0003-scripts-run-in-an-offline-runner-container.md) for the details. Running the development commands directly on your machine does not provide that container separation.

The browser a Script drives does not have your Lookout Session. Reaching Lookout lets a page load the public app, check its health and whether an Account exists, attempt login, or attempt first-launch setup while no Account exists, which never happens when the Account is set from `LOOKOUT_USERNAME` and `LOOKOUT_PASSWORD`. It does not give the page your logged-in access to Watches or data. Other services published on the same Docker host have their own access controls.

## Protect your installation

- **Use a strong, unique Account password.** Someone who can log in can upload and run Scripts. Pages and other LAN devices can attempt login; this version does not limit failed login attempts.
- **Set `LOOKOUT_USERNAME` and `LOOKOUT_PASSWORD` before the first start.** Until the Account exists, whoever reaches setup first can create it, including a page loaded by another Lookout installation on the same host. With both set, Lookout creates the Account from them before it accepts requests, so setup is never open, and applies them on every start, so changing them changes or recovers the password. Without them, complete setup in the app immediately after the first start.
- **Keep Lookout off the public internet.** It is intended for your LAN. Use your own VPN to reach it from elsewhere. Adding HTTPS does not make it ready for public internet exposure ([deployment decision](../adr/0002-designed-for-lan-only-homelab-deployment.md)).
- **Choose the network access you need.** Use VPN Watches if you want the pages they load kept off other LAN devices. If access to your LAN or services on the Docker host matters, run Lookout on a separate VLAN (a separated part of your network) with access rules, or on a separate Docker host with restricted access to your other devices. The separation must actually prevent access to the devices and services you want to protect.

The [decision and probe results](../adr/0007-scripts-are-trusted-and-direct-runs-reach-the-lan.md) explain the accepted exposure and the restrictions considered. For installation steps, see [Deploying](../../README.md#deploying).
