# Scripts run in an offline runner container

Scripts run in a long-lived `runner` container that is connected only to an internal Docker network (`internal: true`), from which it can reach Browserless and be reached by the backend. Nothing is mounted into it: no database, no secrets, no backend configuration. Script source arrives with each Run request. Each Run is a child Node process with a timeout, a memory cap and Node's `--permission` file-read restrictions. Only Browserless has internet access, so a Script's own Node code (`fetch`, `net`) cannot reach the internet; only the browser it drives can. This contains a Script its author didn't fully read, or one uploaded with a stolen login, without the backend needing the Docker socket.

## Considered Options

- **A fresh container per Run.** Rejected. It isolates Scripts from each other, but the backend would need the Docker socket, which is effectively root on the host. That is a worse hole than the one it closes.
- **Node's permission model or `node:vm` alone.** Rejected. Node's docs say neither is a security boundary, and Node 24 has no network permission flag.

## Consequences

- Scripts can only import Node built-ins and `playwright`, the only package installed in the runner.
- Scripts that need data from an API must get it through the browser (e.g. `page.request` or by navigating to it), not through Node.

Sources: [docs/research/script-execution-and-vpn.md](../research/script-execution-and-vpn.md)
