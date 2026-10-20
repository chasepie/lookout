import { defineConfig, devices } from '@playwright/test';
import { ownerStorageState } from './e2e/owner';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5117',
    trace: 'retain-on-failure',
  },
  projects: [
    // First launch happens once per backend, so it runs first and logs in the remaining tests.
    { name: 'first launch', testMatch: /\.setup\.ts$/, use: devices['Desktop Chrome'] },
    {
      name: 'chromium',
      dependencies: ['first launch'],
      use: { ...devices['Desktop Chrome'], storageState: ownerStorageState },
    },
  ],
  webServer: {
    command: 'dotnet run --project ../../tests/Frontend.TestHost --no-launch-profile',
    url: 'http://127.0.0.1:5117/health',
    reuseExistingServer: false,
    timeout: 60_000,
    gracefulShutdown: { signal: 'SIGINT', timeout: 5_000 },
  },
});
