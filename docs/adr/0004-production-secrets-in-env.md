# Production secrets live in the deployment's .env

Every production secret is a Compose variable in the deployment's `.env`, reaching each container in its environment,
rather than a Compose secret file mounted at `/run/secrets`. Secret files would keep secrets out of containers'
environments and `docker inspect`, but browserless only reads its token from its environment, so some secrets would have
been in `.env` anyway, and one place to set them is simpler than two. Compose's secret files are plaintext on the host
too, unlike Swarm's, and `docker inspect` shows environments only to those who can already run containers on the host.

## Consequences

- `.env` must be kept unreadable to other users, and values with `$`, `#` or spaces single-quoted, since Compose
  interpolates them.
- Scripts still don't see the Runner's own secrets, since the Runner starts each with a clean environment; they get only
  the browserless token and, in VPN runs, the proxy password, which the Runner masks in their output.
