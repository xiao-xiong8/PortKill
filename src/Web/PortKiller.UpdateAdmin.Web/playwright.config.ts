import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, retries: 0,
  outputDir: '../../../.local/artifacts/admin-browser/tests',
  use: { baseURL: 'http://127.0.0.1:15073', channel: process.env.PORTKILL_TEST_BROWSER_CHANNEL ?? 'chromium', trace: 'retain-on-failure' },
  webServer: [
    { command: 'node scripts/start-test-api.mjs', url: 'http://127.0.0.1:15081/health/live', reuseExistingServer: false, timeout: 30000 },
    { command: 'pnpm dev --port 15073', url: 'http://127.0.0.1:15073', reuseExistingServer: false, timeout: 30000, env: { PORTKILL_ADMIN_DEV_PROXY: 'http://127.0.0.1:15081' } },
  ],
});
