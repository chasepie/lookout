# @chasepie/lookout-watch-sdk

Types for writing [Lookout](https://github.com/chasepie/lookout) watch scripts, and `lookout-watch`, a CLI that runs one
in Chromium the way Lookout does. Use it to write and try scripts in a project of your own, then upload each one to a
watch in Lookout.

## Installing

The package is on GitHub Packages, whose npm registry needs a token to install from, even for public packages. Create a
[personal access token (classic)](https://github.com/settings/tokens) with the `read:packages` scope, and point the
`@chasepie` scope at GitHub Packages in your project's `.npmrc`:

```ini
@chasepie:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${GITHUB_PACKAGES_TOKEN}
```

With `GITHUB_PACKAGES_TOKEN` set in your environment (or in CI's), install the SDK of the Lookout release you run (see
below), and Node's types, which Playwright's need:

```sh
npm install --save-dev @chasepie/lookout-watch-sdk@0.4 @types/node typescript
```

npm installs the Playwright version the SDK needs with it, as a peer dependency.

### Which version

Lookout runs every script against the SDK and Playwright in its own Runner image, not the ones your project installed,
and each Lookout release publishes the SDK at its own version. So install the SDK that matches the release you run, its
`LOOKOUT_IMAGE_TAG`:

| Lookout image tag | SDK |
| --- | --- |
| A minor version before 1.0, such as `0.4` | `@0.4`, which npm saves as `^0.4.3`: like the image tag, it follows `0.4` |
| A major version from 1.0, such as `1` | `@1`, saved as `^1.2.0` |
| A release, such as `0.4.3` | `--save-exact @0.4.3` |
| `latest` | `@latest` |

A Lookout release that would break your scripts is a new minor version before 1.0 (`0.5.0`), and a new major version
after, so neither your image tag nor the SDK's range moves to it on its own.

## Writing a script

A watch script is one `.ts` or `.js` file whose default export is a class, or an object, implementing
`LookoutWatchScript`. Its `run` gets a Playwright page and a signal that aborts when the run is cancelled or times out,
and returns what it found as a success, or a problem it recognised as a known failure:

```ts
import type { LookoutWatchScript, RunResult } from '@chasepie/lookout-watch-sdk';
import type { Page } from 'playwright';

export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    await page.goto('https://shop.example/products/123', { signal });

    if (await page.getByText('Not available in your country').isVisible()) {
      return { success: false, failureReason: 'The shop blocks this region' };
    }

    const price = await page.locator('.price').innerText({ signal });
    return { success: true, content: price.trim() };
  }
}
```

Anything else it does, such as throwing or returning something else, ends the run as a crash. It may also return a PNG
or JPEG of at most 8 MiB as its `screenshot`, such as `await page.screenshot()`.

Lookout runs only the file you upload, so a script must:

- **Be one file** of at most 64 KiB. It can't import other files of your project.
- **Import only the SDK, Playwright and Node's built-in modules**, such as `node:crypto`. Other packages aren't in the
  Runner, and its sandbox stops a script reading files other than itself and them, or starting processes.
- **Use only TypeScript that Node can strip**, since Node runs `.ts` files without compiling them: `import type` for
  types, and no enums, namespaces or parameter properties. TypeScript's `erasableSyntaxOnly` and `verbatimModuleSyntax`
  options check this.

A `tsconfig.json` for a project of scripts:

```json
{
  "compilerOptions": {
    "module": "nodenext",
    "target": "esnext",
    "types": ["node"],
    "strict": true,
    "noEmit": true,
    "erasableSyntaxOnly": true,
    "verbatimModuleSyntax": true
  }
}
```

and `"type": "module"` in its `package.json`.

## Running a script

`lookout-watch run` runs a script and prints its result, needing Node 22.18 or later for `.ts` files:

```sh
npx lookout-watch run my-watch.ts
```

By default it opens Chromium on your machine with a window, or without one with `--headless`, which
`npx playwright install chromium` installs. To run it on a browserless server instead, as Lookout does, pass
`--browserless <url>` or set `BROWSERLESS_URL`. `npx lookout-watch run --help` lists the other options, such as sending
the browser's traffic through a proxy and saving a screenshot.
