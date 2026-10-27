# Scripts run in an offline runner container

Scripts run in a long-lived `runner` container that is connected only to internal Docker networks (`internal: true`), from which it can reach Browserless and be reached by the backend. Nothing is mounted into it: no database, no secrets, no backend configuration. Script source arrives with each Run request. Each Run is a child Node process with a timeout, a memory cap and Node's `--permission` file-read restrictions. Only the browser it drives has a route to the internet; a Script's own Node code (`fetch`, `net`) has no direct internet access. This keeps Scripts separate from Lookout's database and secrets without the backend needing the Docker socket. Scripts are trusted code; these precautions do not promise to contain a hostile Script ([ADR-0007](0007-scripts-are-trusted-and-direct-runs-reach-the-lan.md)).

Offline here means no direct route to the internet, not necessarily no access to the Docker host's published ports. A Script's Node code may reach those ports through the runner's internal networks on Linux Docker Engine; this is expected from Docker's documentation, not verified. The browser it drives can reach the LAN in Direct Runs. In VPN Runs, gluetun blocks browser access to the LAN but still lets it reach the host's published ports through its own Docker networks. Selecting a VPN Watch does not change the runner's networks. ADR-0007 records the evidence and why this reach is accepted.

## Considered Options

- **A fresh container per Run.** Rejected. It isolates Scripts from each other, but the backend would need the Docker socket, which is effectively root on the host. That is a worse hole than the one it closes.
- **Node's permission model or `node:vm` alone.** Rejected. Node's docs say neither is a security boundary, and Node 24 has no network permission flag.

## Consequences

- Scripts can only import Node built-ins and `playwright`, the only package installed in the runner.
- Scripts that need data from a public internet API must get it through the browser (e.g. `page.request` or by navigating to it), not directly through Node.
- The runner requires a token from Lookout, so that Browserless, which can reach it, can't have it run a Script. It comes in the runner's environment, not mounted. Scripts' processes aren't given it, and, connecting to Browserless, can't start a process to read it from the runner's. The VPN-provider login also stays out of the runner. Optional HTTP proxy credentials are passed into Script processes to configure their browsers and are visible to Scripts; ADR-0007 records why this is accepted.

Sources: [docs/research/script-execution-and-vpn.md](../research/script-execution-and-vpn.md)
