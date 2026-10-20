import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

test('runs within its Content Security Policy and Trusted Types', async ({ page }) => {
  await page.addInitScript(() => {
    const violations: string[] = [];
    Object.assign(window, { cspViolations: violations });
    document.addEventListener('securitypolicyviolation', (event) => {
      violations.push(`${event.effectiveDirective} blocked ${event.blockedURI || 'inline code'}`);
    });
  });
  const name = `Policy check ${randomUUID()}`;
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}'),
  });
  await page.getByRole('button', { name: 'Create Watch' }).click();
  const watch = page.getByRole('article', { name });
  await watch.getByRole('button', { name: 'Run now' }).click();
  await expect(watch).toContainText('£19.99');

  // The global stylesheet gives buttons a pointer cursor, so this shows it loaded and applied.
  await expect(watch.getByRole('button', { name: 'Run now' })).toHaveCSS('cursor', 'pointer');
  expect(await page.evaluate(() => (window as unknown as { cspViolations: string[] }).cspViolations)).toEqual([]);
});
