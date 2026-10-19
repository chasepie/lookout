/**
 * PROTOTYPE for issue #2: answers the browser and VPN questions with running code.
 * Throwaway. It lives only on the prototype/browser-and-vpn branch and is never merged into main.
 *
 *   node prototype.ts bot [mode] [--proxy]  Q1 bot detection (with --proxy: Q3, does the flag hold when proxied?)
 *   node prototype.ts load            Q1 time to the load event per mode, and Browserless memory
 *   node prototype.ts fail-closed     Q2 does the gluetun proxy fail closed when the VPN is down? (ends with webrtc)
 *   node prototype.ts webrtc [--screenshots]  Q2 does WebRTC reveal the home IP around the proxy?
 *   node prototype.ts probe           the exit IP through the proxy, over HTTPS, plain HTTP and an IP literal
 *   node prototype.ts rotate [count]  Q3 does stopped-then-running change the exit IP, and how fast?
 *
 * Every line printed is masked so the home IP never lands in a committed log or screenshot. Direct-mode retailer
 * text can still show the home location, so scrub logs before committing them (see README).
 */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright';

const BROWSERLESS = 'ws://127.0.0.1:3000/chromium/playwright';
const CONTROL = 'http://127.0.0.1:8000';
const API_KEY = 'prototype-only-not-a-secret';
const PROXY = { server: 'http://gluetun:8888' };
const STEALTH_FLAG = '--disable-blink-features=AutomationControlled';
const WEBRTC_FLAG = '--webrtc-ip-handling-policy=disable_non_proxied_udp';
const RESULTS = new URL('./results/', import.meta.url);
const COMPOSE_FILE = new URL('./compose.yml', import.meta.url).pathname;

const IP_URLS = {
  https: 'https://api.ipify.org/?format=json',
  http: 'http://api.ipify.org/?format=json',
  ipLiteral: 'https://1.1.1.1/cdn-cgi/trace',
};

const RETAILERS = [
  'https://www.bestbuy.com/',
  'https://www.walmart.com/',
  'https://www.target.com/',
  'https://www.amazon.com/',
  'https://www.nike.com/',
];
const BLOCKED = /access denied|robot or human|are you a (robot|human)|captcha|just a moment|unusual traffic|request blocked|pardon our interruption|verify you are human/i;

let homeIp = '';
const started = Date.now();
const mask = (text: string) => (homeIp ? text.replaceAll(homeIp, '<home-ip>') : text);
const log = (event: string, data: Record<string, unknown> = {}) =>
  console.log(mask(JSON.stringify({ t: ((Date.now() - started) / 1000).toFixed(2), event, ...data })));
const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
const firstLine = (error: unknown) => (error instanceof Error ? error.message : String(error)).split('\n')[0];
const compose = (...args: string[]) => execFileSync('docker', ['compose', '-f', COMPOSE_FILE, ...args], { encoding: 'utf8' });

function connect(headless: boolean, args = [STEALTH_FLAG], launchOptions: Record<string, unknown> = {}) {
  const launch = encodeURIComponent(JSON.stringify({ headless, args, ...launchOptions }));
  return chromium.connect(`${BROWSERLESS}?launch=${launch}`);
}

async function control(method: 'GET' | 'PUT', path: string, body?: unknown, apiKey = API_KEY) {
  const response = await fetch(`${CONTROL}${path}`, {
    method,
    headers: { 'X-API-Key': apiKey, 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  return { status: response.status, body: (await response.text()).trim() };
}

async function vpnStatus() {
  const { body } = await control('GET', '/v1/vpn/status');
  return (JSON.parse(body) as { status: string }).status;
}

async function publicIp() {
  const { body } = await control('GET', '/v1/publicip/ip');
  return JSON.parse(body) as { public_ip: string; [key: string]: unknown };
}

/** Polls until the check returns a value, returning it and how long it took */
async function pollUntil<T>(check: () => Promise<T | undefined>, timeoutMs: number, intervalMs = 250) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const value = await check().catch(() => undefined);
    if (value !== undefined) {
      return { value, ms: Date.now() - start };
    }
    await sleep(intervalMs);
  }
  return { value: undefined, ms: Date.now() - start };
}

/** What a page sees as its exit IP, or the error that stopped it */
async function exitIp(page: Page, url: string, timeout = 15_000) {
  try {
    const response = await page.goto(url, { timeout });
    const text = await page.locator('body').innerText();
    const ip = /(?:"ip":"|ip=)([\d.]+)/.exec(text)?.[1];
    return { ok: true, status: response?.status(), ip, leak: ip === homeIp };
  } catch (error) {
    return { ok: false, error: firstLine(error) };
  }
}

/** Replaces the home IP in the rendered page so screenshots can be committed */
async function maskPage(page: Page) {
  if (!homeIp) {
    return;
  }
  await page.evaluate(`(() => {
    const ip = ${JSON.stringify(homeIp)};
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.nodeValue.includes(ip)) node.nodeValue = node.nodeValue.replaceAll(ip, '<home-ip>');
    }
    for (const input of document.querySelectorAll('input')) input.value = input.value.replaceAll(ip, '<home-ip>');
  })()`).catch(() => undefined);
}

const FINGERPRINT = `(() => {
  const gl = document.createElement('canvas').getContext('webgl');
  const debug = gl && gl.getExtension('WEBGL_debug_renderer_info');
  return {
    webdriver: navigator.webdriver,
    userAgent: navigator.userAgent,
    brands: navigator.userAgentData ? navigator.userAgentData.brands.map(b => b.brand + ' ' + b.version).join(', ') : null,
    platform: navigator.platform,
    languages: navigator.languages.join(','),
    hardwareConcurrency: navigator.hardwareConcurrency,
    plugins: navigator.plugins.length,
    webgl: debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : null,
    screen: screen.width + 'x' + screen.height,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  };
})()`;

/** The public IPs WebRTC reveals through STUN (server-reflexive candidates), which bypass an HTTP proxy */
const WEBRTC_IPS = `new Promise(resolve => {
  const ips = new Set();
  const pc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] });
  const done = () => { pc.close(); resolve([...ips]); };
  pc.createDataChannel('probe');
  pc.onicecandidate = event => {
    if (!event.candidate) return done();
    const match = / ([0-9]{1,3}(?:\\.[0-9]{1,3}){3}) [0-9]+ typ srflx/.exec(event.candidate.candidate);
    if (match) ips.add(match[1]);
  };
  pc.createOffer().then(offer => pc.setLocalDescription(offer));
  setTimeout(done, 8000);
})`;

async function waitForVpn(timeoutMs = 120_000) {
  const { value, ms } = await pollUntil(async () => {
    if ((await vpnStatus()) !== 'running') {
      return undefined;
    }
    const ip = await publicIp();
    return ip.public_ip ? ip : undefined;
  }, timeoutMs, 1000);
  if (!value) {
    throw new Error(`VPN not running with a public IP after ${ms} ms`);
  }
  log('vpn-ready', { waitedMs: ms, ...value });
  return value;
}

// Q1 (and Q3's "does the flag hold when proxied?")

const BOT_MODES = {
  'local-headful': () => chromium.launch({ headless: false, args: [STEALTH_FLAG] }),
  'browserless-headless': () => connect(true),
  'browserless-headless-no-flag': () => connect(true, []),
  'browserless-headless-ua': () => connect(true),
  'browserless-headless-ua-webrtc': () => connect(true, [STEALTH_FLAG, WEBRTC_FLAG]),
  'browserless-headful': () => connect(false),
} satisfies Record<string, () => Promise<Browser>>;

/** Headless Chromium's user agent minus its "HeadlessChrome" token, set per context by the runner */
const headfulUserAgent = (browser: Browser) =>
  `Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${browser.version().split('.')[0]}.0.0.0 Safari/537.36`;

async function browserscan(context: BrowserContext, name: string) {
  const page = await context.newPage();
  await page.goto('https://www.browserscan.net/bot-detection', { timeout: 60_000 });
  const verdict = page.getByText('Test Results:', { exact: true }).locator('xpath=following-sibling::strong');
  await verdict.waitFor({ timeout: 60_000 });
  await sleep(3000);
  const fingerprint = await page.evaluate(FINGERPRINT);
  await maskPage(page);
  await page.screenshot({ path: new URL(`${name}-browserscan.jpg`, RESULTS).pathname, fullPage: true, quality: 60 });
  writeFileSync(new URL(`${name}-browserscan.txt`, RESULTS), mask(await page.locator('body').innerText()));
  log('browserscan', { mode: name, verdict: (await verdict.innerText()).trim(), fingerprint });
  await page.close();
}

/** Direct pages show the home location (e.g. Target's "Ship to <ZIP>"), so only proxied pages are screenshotted */
async function retailer(context: BrowserContext, name: string, url: string, screenshot: boolean) {
  const page = await context.newPage();
  const site = new URL(url).hostname.replace(/^www\./, '').replace(/\.com$/, '');
  try {
    const response = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
    await sleep(6000);
    const title = await page.title();
    const text = (await page.locator('body').innerText().catch(() => '')).replace(/\s+/g, ' ');
    const status = response?.status();
    const blocked = (status ?? 0) >= 400 || BLOCKED.test(title) || BLOCKED.test(text.slice(0, 3000));
    if (screenshot) {
      await maskPage(page);
      await page.screenshot({ path: new URL(`${name}-${site}.jpg`, RESULTS).pathname, quality: 60 });
    }
    log('retailer', { mode: name, site, status, blocked, title, text: text.slice(0, 160) });
  } catch (error) {
    log('retailer', { mode: name, site, blocked: true, error: firstLine(error) });
  } finally {
    await page.close();
  }
}

async function bot(proxied: boolean, only?: string) {
  mkdirSync(RESULTS, { recursive: true });
  if (proxied) {
    await waitForVpn();
  }
  for (const [mode, launch] of Object.entries(BOT_MODES)) {
    if ((only && mode !== only) || (proxied && mode === 'local-headful')) {
      continue;
    }
    const name = `q1-${mode}${proxied ? '-proxied' : ''}`;
    const browser = await launch();
    try {
      const context = await browser.newContext({
        ...(proxied ? { proxy: PROXY } : {}),
        ...(mode.includes('-ua') ? { userAgent: headfulUserAgent(browser) } : {}),
      });
      const ipPage = await context.newPage();
      log('context', { mode: name, browserVersion: browser.version(), ...(await exitIp(ipPage, IP_URLS.https)) });
      await ipPage.close();
      await browserscan(context, name).catch(error => log('browserscan', { mode: name, error: firstLine(error) }));
      for (const url of RETAILERS) {
        await retailer(context, name, url, proxied);
      }
    } finally {
      await browser.close();
    }
  }
}

/** How long each retailer takes to fire `load` (page.goto's default) in each Browserless mode, plus memory */
async function load() {
  const memory = () => execFileSync('docker', ['stats', '--no-stream', '--format', '{{.MemUsage}}', 'lookout-prototype-browserless-1'], { encoding: 'utf8' }).trim();
  log('memory', { idle: memory() });
  for (const mode of ['browserless-headless', 'browserless-headless-ua', 'browserless-headful'] as const) {
    const browser = await BOT_MODES[mode]();
    try {
      const context = await browser.newContext(mode.includes('-ua') ? { userAgent: headfulUserAgent(browser) } : {});
      for (const url of RETAILERS) {
        const page = await context.newPage();
        const start = Date.now();
        const error = await page.goto(url, { waitUntil: 'load', timeout: 60_000 }).then(() => undefined, firstLine);
        log('load', { mode, site: new URL(url).hostname, ms: Date.now() - start, error, memory: memory() });
        await page.close();
      }
    } finally {
      await browser.close();
    }
  }
}

// Q2

async function probe(label: string, context: BrowserContext, timeout = 10_000) {
  const page = await context.newPage();
  const results: Record<string, unknown> = {};
  for (const [kind, url] of Object.entries(IP_URLS)) {
    results[kind] = await exitIp(page, url, timeout);
  }
  await page.close();
  log('probe', { label, ...results });
}

/** Stops then starts the VPN through the control API, so gluetun picks a new random server */
async function reconnect() {
  const stopped = await control('PUT', '/v1/vpn/status', { status: 'stopped' });
  const running = await control('PUT', '/v1/vpn/status', { status: 'running' });
  return { stopped: stopped.body, running: running.body };
}

/**
 * Requests through the proxy as fast as possible until it works, flagging any request that leaks the home IP.
 * Each attempt gets a fresh context: a reused page queues behind CONNECTs it abandoned while the tunnel was down.
 * A server that never comes up is re-rolled after rerollAfterMs, as a rotator would.
 */
async function waitUntilUsable(label: string, browser: Browser, timeoutMs = 300_000, rerollAfterMs = 15_000) {
  const start = Date.now();
  const errors = new Map<string, number>();
  let lastReroll = start;
  let attempts = 0;
  let leaks = 0;
  let rerolls = 0;
  while (Date.now() - start < timeoutMs) {
    attempts++;
    const context = await browser.newContext({ proxy: PROXY });
    const result = await exitIp(await context.newPage(), IP_URLS.ipLiteral, 3000);
    await context.close();
    if (result.ok && result.leak) {
      leaks++;
      log('LEAK', { label, attempt: attempts, ...result });
    } else if (result.ok && result.ip) {
      const outcome = { label, ms: Date.now() - start, attempts, leaks, rerolls, ip: result.ip, errors: Object.fromEntries(errors) };
      log('proxy-up', outcome);
      return outcome;
    } else {
      const key = (result.error ?? `status ${result.status}`).replace(/ at https?:\S+/, '');
      errors.set(key, (errors.get(key) ?? 0) + 1);
      if (Date.now() - lastReroll > rerollAfterMs) {
        rerolls++;
        lastReroll = Date.now();
        log('reroll', { label, afterMs: Date.now() - start, vpnStatus: await vpnStatus().catch(firstLine), ...(await reconnect().catch(error => ({ error: firstLine(error) }))) });
      }
      await sleep(200);
    }
  }
  const outcome = { label, ms: Date.now() - start, attempts, leaks, rerolls, ip: undefined, errors: Object.fromEntries(errors) };
  log('proxy-never-up', outcome);
  return outcome;
}

/** Hides WebRTC from pages, so nothing can ask STUN for the real address */
const NO_WEBRTC = `for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel']) delete window[name];`;

const WEBRTC_VARIANTS = [
  { label: 'context proxy', args: [STEALTH_FLAG] },
  { label: 'context proxy + force-webrtc-ip-handling-policy', args: [STEALTH_FLAG, '--force-webrtc-ip-handling-policy=disable_non_proxied_udp'] },
  { label: 'context proxy + webrtc-ip-handling-policy', args: [STEALTH_FLAG, WEBRTC_FLAG] },
  { label: 'launch proxy', args: [STEALTH_FLAG], launchProxy: true },
  { label: 'launch proxy + force-webrtc-ip-handling-policy', args: [STEALTH_FLAG, '--force-webrtc-ip-handling-policy=disable_non_proxied_udp'], launchProxy: true },
  { label: 'context proxy + WebRTC removed by init script', args: [STEALTH_FLAG], removeWebRtc: true },
];

/** Whether WebRTC reveals the home IP from a proxied browser, for each way of setting the proxy up */
async function webrtc(screenshots = false) {
  for (const variant of WEBRTC_VARIANTS) {
    const browser = await connect(true, variant.args, variant.launchProxy ? { proxy: PROXY } : {});
    try {
      const context = await browser.newContext(variant.launchProxy ? {} : { proxy: PROXY });
      if (variant.removeWebRtc) {
        await context.addInitScript(NO_WEBRTC);
      }
      const page = await context.newPage();
      const exit = await exitIp(page, IP_URLS.https);
      const ips = await page.evaluate(WEBRTC_IPS).catch(firstLine);
      log('webrtc', { ...variant, exitIp: exit.ip, srflxIps: ips, leaksHomeIp: Array.isArray(ips) && ips.includes(homeIp) });
      if (screenshots) {
        await page.goto('https://www.browserscan.net/webrtc', { timeout: 60_000 });
        await sleep(10_000);
        await maskPage(page);
        const name = variant.label.toLowerCase().replace(/[^a-z]+/g, '-');
        await page.screenshot({ path: new URL(`q2-webrtc-${name}.jpg`, RESULTS).pathname, fullPage: true, quality: 60 });
      }
    } finally {
      await browser.close();
    }
  }
}

async function failClosed() {
  await waitForVpn();
  const browser = await connect(true);
  try {
    await probe('direct, VPN up', await browser.newContext());
    const proxied = await browser.newContext({ proxy: PROXY });
    await probe('proxied, VPN up', proxied);

    log('put', await control('PUT', '/v1/vpn/status', { status: 'stopped' }));
    log('vpn-status', { status: await vpnStatus(), publicIp: await publicIp() });
    await probe('proxied (existing context), VPN stopped', proxied, 5000);
    await probe('proxied (new context), VPN stopped', await browser.newContext({ proxy: PROXY }), 5000);
    await sleep(30_000);
    await probe('proxied, VPN stopped for 30 s', proxied, 5000);

    log('put', await control('PUT', '/v1/vpn/status', { status: 'running' }));
    await waitUntilUsable('VPN restarting via control API', browser);
    await probe('proxied, VPN back up', proxied);

    log('compose', { out: compose('stop', 'gluetun').trim() });
    await probe('proxied, gluetun container stopped', proxied, 5000);
    log('compose', { out: compose('start', 'gluetun').trim() });
    await waitUntilUsable('gluetun container starting', browser);
    await probe('proxied, gluetun container back up', proxied);
  } finally {
    await browser.close();
  }

  await webrtc();
}

// Q3

async function rotate(count: number) {
  log('auth', {
    'GET /v1/vpn/settings (not in role)': (await control('GET', '/v1/vpn/settings')).status,
    'GET /v1/vpn/status (wrong key)': (await control('GET', '/v1/vpn/status', undefined, 'wrong')).status,
    'GET /v1/vpn/status (role key)': (await control('GET', '/v1/vpn/status')).status,
  });
  await waitForVpn();

  const browser = await connect(true);
  const context = await browser.newContext({ proxy: PROXY });
  const page = await context.newPage();
  let previous = (await exitIp(page, IP_URLS.ipLiteral)).ip;
  log('initial', { ip: previous });

  const rows: { ip: string | undefined; location: string | undefined; usableMs: number; rerolls: number }[] = [];
  try {
    for (let i = 1; i <= count; i++) {
      const since = new Date().toISOString();
      const status = { before: await vpnStatus(), put: await reconnect(), after: await vpnStatus() };
      const usable = await waitUntilUsable(`rotation ${i}`, browser);
      const reported = await pollUntil(async () => {
        const ip = await publicIp();
        return ip.public_ip ? ip : undefined;
      }, 30_000, 500);
      const logs = compose('logs', '--no-color', '--since', since, 'gluetun')
        .split('\n')
        .filter(line => /Peer Connection Initiated|Initialization Sequence Completed|failed|refused|ERROR|WARN/.test(line))
        .map(line => line.replace(/^\S+\s+\|\s+\S+\s+/, ''));
      const location = reported.value ? `${String(reported.value['city'])}, ${String(reported.value['region'])}` : undefined;
      log('rotation', {
        rotation: i,
        status,
        usableMs: usable.ms,
        rerolls: usable.rerolls,
        ip: usable.ip,
        changed: usable.ip !== previous,
        gluetunPublicIp: reported.value?.public_ip,
        location,
        logs,
      });
      rows.push({ ip: usable.ip, location, usableMs: usable.ms, rerolls: usable.rerolls });
      previous = usable.ip;
      await sleep(5000);
    }
  } finally {
    await browser.close();
  }

  const times = rows.map(row => row.usableMs).sort((a, b) => a - b);
  log('summary', {
    rotations: rows.length,
    distinctIps: new Set(rows.map(row => row.ip)).size,
    distinctLocations: [...new Set(rows.map(row => row.location))],
    changedEveryTime: rows.every((row, i) => i === 0 || row.ip !== rows[i - 1]?.ip),
    neverUsable: rows.filter(row => !row.ip).length,
    rotationsNeedingReroll: rows.filter(row => row.rerolls > 0).length,
    usableMs: { min: times[0], median: times[Math.floor(times.length / 2)], max: times.at(-1) },
  });
}

async function main() {
  homeIp = (await (await fetch('https://api.ipify.org')).text()).trim();
  log('start', { command: process.argv.slice(2).join(' '), homeIpKnown: Boolean(homeIp) });
  const [command, arg] = process.argv.slice(2);
  switch (command) {
    case 'bot': {
      const args = process.argv.slice(3);
      return bot(args.includes('--proxy'), args.find(a => !a.startsWith('--')));
    }
    case 'load':
      return load();
    case 'fail-closed':
      return failClosed();
    case 'probe': {
      await waitForVpn();
      const browser = await connect(true);
      await probe('proxied', await browser.newContext({ proxy: PROXY }));
      return browser.close();
    }
    case 'webrtc':
      return webrtc(arg === '--screenshots');
    case 'rotate':
      return rotate(Number(arg ?? 8));
    default:
      throw new Error('usage: node prototype.ts bot [--proxy | mode] | fail-closed | rotate [count]');
  }
}

await main();
