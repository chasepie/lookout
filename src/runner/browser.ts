/**
 * Where a Script's browser comes from, by provider: what a Script's process must be allowed to do for it, and how
 * that process opens a page in it. The runner asks the first as it starts, and each Script's process the second.
 * Playwright is imported only once a provider needs it, since a runner that connects to Browserless needs none of it.
 */
import { basename, dirname } from 'node:path';
import type { Browser, BrowserContextOptions, Page } from 'playwright';

/** The proxy for a Script's browser context, as Playwright's `proxy` option */
export type ProxySettings = NonNullable<BrowserContextOptions['proxy']>;

/** Where a Script's browser comes from */
export type BrowserSettings =
  /** A Chromium the Script's process launches itself, for local development and tests; non-headless is for debugging */
  | { provider: 'launch'; headless: boolean }
  /** A headful Chromium from Browserless, at its Playwright endpoint `url`, as in deployment */
  | { provider: 'connect'; url: string };

/**
 * Chromium's flags, whichever way it's started. The first hides that it's automated (`navigator.webdriver`). The
 * second keeps WebRTC from revealing the home IP around a VPN Run's proxy, and is harmless without one
 * (docs/research/browser-and-vpn-prototype.md).
 */
const browserArgs = [
  '--disable-blink-features=AutomationControlled',
  '--webrtc-ip-handling-policy=disable_non_proxied_udp'
];

/** Where Playwright installs its browsers, which it reads from to launch Chromium */
async function findBrowsersDirectory() {
  const { chromium } = await import('playwright');
  // The executable is nested in a directory for its build, such as chromium-1248, inside the install directory
  for (let dir = chromium.executablePath(); dir !== dirname(dir); dir = dirname(dir)) {
    if (basename(dir).startsWith('chromium-')) {
      return dirname(dir);
    }
  }
  throw new Error(`Can't find Playwright's browsers directory from ${chromium.executablePath()}`);
}

/**
 * The OS's name, which Playwright reads on Linux to tell which build of Chromium to launch. It's granted even where
 * it doesn't exist, since under the permission model even checking whether a file exists throws for a path that
 * isn't granted.
 */
const osReleaseFile = '/etc/os-release';

/**
 * What a Script's process may do, on top of what every one may, to get a browser from `settings`. To launch
 * Chromium, it needs to read the browsers and the OS's name, and to start processes, since Chromium is one. That
 * allowance lets a Script get around the file limits, and start a process that outlives it, so a process that
 * connects to Browserless doesn't get it.
 */
export async function browserPermissionArgs(settings: BrowserSettings): Promise<string[]> {
  if (settings.provider === 'connect') {
    return [];
  }

  const browsersDirectory = await findBrowsersDirectory();
  return [
    ...[browsersDirectory, osReleaseFile].map((path) => `--allow-fs-read=${path}`),
    '--allow-child-process',
    // Node warns about that allowance on every start, which would end up in every Script's console output
    '--disable-warning=PERM0002',
  ];
}

/** A browser, and how to be done with it */
interface OpenBrowser {
  browser: Browser;
  close: () => Promise<void>;
}

/**
 * Launches a local Chromium as a server, since only that tells which process is Chromium. It's passed to
 * `onProcess`, since Playwright puts it in a process group of its own, which must be killed along with this one's.
 */
async function launchBrowser(headless: boolean, onProcess: (pid: number) => void): Promise<OpenBrowser> {
  const { chromium } = await import('playwright');

  const browserServer = await chromium.launchServer({
    headless,
    args: browserArgs
  });

  const { pid } = browserServer.process();
  if (pid !== undefined) {
    onProcess(pid);
  }

  // Closing the server closes Chromium, and with it its contexts
  const close = () => browserServer.close();
  try {
    return {
      browser: await chromium.connect(browserServer.wsEndpoint()),
      close
    };
  } catch (error) {
    await close();
    throw error;
  }
}

/**
 * Connects to a headful Chromium in Browserless, which launches one per connection with the options in its
 * `launch` query parameter, on the virtual display (Xvfb) its image starts. Headless Chromium's user agent says
 * `HeadlessChrome`, which on its own makes bot detection take it for a robot; headful's is Chrome's own everywhere,
 * workers included, and keeps up with Browserless upgrades (docs/research/browser-and-vpn-prototype.md). Chromium
 * isn't this process's to kill: Browserless closes it once the connection closes, even when this process is killed.
 */
async function connectBrowser(url: string): Promise<OpenBrowser> {
  const { chromium } = await import('playwright');

  const endpoint = new URL(url);
  const launchOptions = {
    headless: false,
    args: browserArgs
  };
  endpoint.searchParams.set('launch', JSON.stringify(launchOptions));

  const browser = await chromium.connect(endpoint.href);
  return {
    browser,
    close: () => browser.close()
  };
}

/** A page for a Script, and how to be done with its browser */
export interface ScriptPage {
  page: Page;
  close: () => Promise<void>;
}

/**
 * Opens a page for a Script in a browser from `settings`, in a new context that goes through `proxy` if there is
 * one. A launched Chromium's process is passed to `onProcess`; one in Browserless has none here.
 */
export async function openPage(settings: BrowserSettings, { proxy, onProcess }: {
  proxy?: ProxySettings | undefined;
  onProcess: (pid: number) => void;
}): Promise<ScriptPage> {
  const { browser, close } = settings.provider === 'connect'
    ? await connectBrowser(settings.url)
    : await launchBrowser(settings.headless, onProcess);

  try {
    const browserContext = await browser.newContext({
      ...(proxy && { proxy })
    });
    return {
      page: await browserContext.newPage(),
      close
    };
  } catch (error) {
    // A failure to clean up must not replace the one that stopped the page opening
    await close().catch(() => undefined);
    throw error;
  }
}
