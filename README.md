# Lookout

Lookout runs watch scripts against web pages, on demand or on a schedule, keeps the result of every run, and notifies
you when something changes.

A **watch** is something you want checked: a **watch script** that drives a real Chromium through Playwright, plus when
and how to run it. Each **run** ends as a **success**, carrying what the script found on the page, a **known failure**,
a problem the script recognised, such as a page that's blocked from the current VPN region, or a **crash**. Lookout
keeps a screenshot of the page with a run when you ask it to. Runs can go through a VPN that rotates between regions,
and Lookout sends a **notification** (through Pushover, so far) when a run shows a content change, a known-failure
streak reaching its threshold, the start of a crash streak, or a recovery. [`GLOSSARY.md`](GLOSSARY.md) defines each of
these.

Lookout is for one person: the **Owner**, who signs in with a username and password set in its settings.

## How it fits together

```mermaid
flowchart LR
    Owner([Owner]) -->|HTTPS, via a reverse proxy| Backend
    Backend -->|notifications| Pushover
    Backend -->|run this watch| Runner
    Backend -->|rotate regions| gluetun
    Runner -->|Playwright| browserless["browserless (Chromium)"]
    browserless -->|watches without the VPN| Web([Web pages])
    browserless -->|watches with the VPN| gluetun["gluetun (VPN)"]
    gluetun --> Web
```

- **Backend** (`src/Backend`): the ASP.NET Core API and the Angular web UI (`src/frontend`) it serves. It stores
  watches and runs in SQLite, schedules runs, works out each run's events and sends notifications.
- **Runner** (`src/Runner`): runs each watch script with the watch CLI, sandboxed by Node's permission model in a
  locked-down container ([`docs/security.md`](docs/security.md)).
- **browserless**: the Chromium the scripts drive.
- **gluetun**: the VPN, as an HTTP proxy for watches that use it.

## Writing watch scripts

A watch script is a single `.ts` or `.js` file whose default export implements `LookoutWatchScript` from the watch SDK
(`src/watch-sdk`):

```ts
import type { LookoutWatchScript, RunResult } from '@chasepie/lookout-watch-sdk';
import type { Page } from 'playwright';

export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    await page.goto('https://shop.example/products/123', { signal });
    const price = await page.locator('.price').innerText({ signal });
    return { success: true, content: price.trim() };
  }
}
```

You upload it when you create or edit a watch. [`examples/`](examples) has a few, and the SDK's
[README](src/watch-sdk/README.md) covers writing them in a project of your own, installing the SDK from GitHub
Packages, and trying them locally with `lookout-watch run`.

## Deploying

Each release publishes the Backend's and Runner's images to GHCR. [`deploy/`](deploy) has the Docker Compose file that
runs them, and its [README](deploy/README.md) walks through the settings, the reverse proxy Lookout needs in front of it
for HTTPS, and which image tag to follow for updates.

## Releasing

Lookout is versioned with [release-please](https://github.com/googleapis/release-please), from PR titles:

- **PR titles are [Conventional Commits](https://www.conventionalcommits.org/)**, since a squash merge makes the title
  the commit on main. `fix: …` releases a patch and `feat: …` a minor version; `feat!: …` is a breaking change, a major
  version. Until 1.0, everything shifts down one: a breaking change is a minor version and a feature a patch. Other
  types, such as `docs:`, `refactor:`, `test:` and `chore:`, release nothing. A check on each PR enforces this.
- **A breaking change** is one a deployment can't take without the Owner: a new `docker-compose.yaml` or setting, or a
  change that breaks existing watch scripts.
- **Merging a PR** updates release-please's Release PR, which holds the next version and its changelog. **Merging the
  Release PR** releases it: it tags `v<version>`, creates a GitHub Release, and publishes the images and the watch SDK
  at that version.
- **Going to 1.0**: merge a PR whose commit message has a `Release-As: 1.0.0` footer.
- **If a release fails to publish**, fix the cause, then run the Release workflow by hand (Actions → Release → Run
  workflow) with its version, such as `0.4.3`. It publishes that release again, skipping an SDK that's already
  published. Only the newest release moves the `0.4`, `1` and `latest` image tags and the SDK's `latest`, so publishing
  an older one again never moves a deployment back.

release-please opens its Release PRs as a GitHub App, since GitHub runs no CI on PRs opened with `GITHUB_TOKEN` and
main's ruleset needs CI to pass. To set it up once:

1. [Create a GitHub App](https://github.com/settings/apps/new) with any unique name, such as `lookout-release-<you>`,
   and the repository's URL as its homepage. Untick "Active" under Webhook. Give it read and write access to the
   repository permissions Contents, Issues and Pull requests, and let it be installed only on your account.
2. On its page, note its Client ID, and generate a private key, which downloads a `.pem` file.
3. Install it on this repository only.
4. In this repository's Actions settings, add the Client ID as the variable `RELEASE_APP_CLIENT_ID`, and the `.pem`
   file's contents as the secret `RELEASE_APP_PRIVATE_KEY`. Then delete the `.pem` file.

If the key is ever exposed, generate a new one, replace the secret, and delete the old key on the App's page.

## Developing

You need the .NET SDK in [`global.json`](global.json), the Node version in [`.nvmrc`](.nvmrc), and Docker.

```sh
npm ci
dotnet run --project src/AppHost
```

The Aspire AppHost starts everything above and opens the Aspire dashboard, with the web UI listed there. It reads these
settings from a `.env` file at the repo root:

| Setting | |
| --- | --- |
| `LOOKOUT_OWNER_USERNAME`, `LOOKOUT_OWNER_PASSWORD` | Required: what you sign in with. The password needs at least 16 characters. |
| `GLUETUN_OPENVPN_USER`, `GLUETUN_OPENVPN_PASSWORD` | Your VPN provider's OpenVPN credentials. gluetun only starts with both. |
| `GLUETUN_VPN_SERVICE_PROVIDER` | gluetun's VPN provider. Default: `private internet access`. |
| `GLUETUN_SERVER_REGIONS` | The VPN regions Lookout may use, comma-separated, as in `DE Berlin,US Texas`. Default: all of them. |
| `TZ` | The time zone of schedules and scripts' dates. Default: `Etc/UTC`. |
| `LOOKOUT_WATCH_SCRIPTS_DIR`, `LOOKOUT_DATABASE_DIR`, `GLUETUN_DATA_DIR` | Where data is kept. Default: a folder for each in `.docker-volumes/`. |

In development, a database with no watches gets one watch for each script in `examples/`.

### Tests and checks

```sh
npm run build -w src/watch-sdk           # the Runner's tests and the TypeScript checks need it built
dotnet test --solution Lookout.slnx      # Backend, Runner and AppHost
npm run test:ci -w src/frontend         # ng test, once
npx playwright install chromium-headless-shell
npm test -w src/watch-sdk
npm run lint && npm run typecheck
dotnet format whitespace Lookout.slnx --verify-no-changes
```

CI (`.github/workflows/ci.yml`) runs all of these on each pull request.

## Documentation

- [`GLOSSARY.md`](GLOSSARY.md): the domain's terms.
- [`docs/security.md`](docs/security.md): signing in, sessions, CSRF protection and how watch scripts are sandboxed.
  Read it before changing anything about them.
- [`docs/adr/`](docs/adr): decisions that would surprise a reader, and why they were made.
- [`deploy/README.md`](deploy/README.md): running Lookout in production.

## License

[MIT](LICENSE)
