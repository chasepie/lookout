// Signs in to Lookout's frontend as the Owner and screenshots a page.
// Usage (from the repo root, which has playwright in node_modules):
//   LOOKOUT_USER=... LOOKOUT_PASSWORD=... node .claude/skills/run-lookout/screenshot.mjs http://localhost:4200 /vpn out.png
// lookout.sh's `screenshot` command fills in the credentials from .env.
import { chromium } from 'playwright';

const [frontend, path, out] = process.argv.slice(2);
if (!frontend || !path || !out) {
  console.error('usage: screenshot.mjs FRONTEND_URL PATH OUT.png');
  process.exit(2);
}

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  await page.goto(new URL('/login', frontend).href);
  await page.locator('#username').fill(process.env.LOOKOUT_USER ?? '');
  await page.locator('#password').fill(process.env.LOOKOUT_PASSWORD ?? '');
  await page.locator('button[type=submit]').click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 15_000 });

  await page.goto(new URL(path, frontend).href);
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: out, fullPage: true });
  console.log(`screenshot: ${out}`);
} finally {
  await browser.close();
}
