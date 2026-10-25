import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, retries: 0,
  outputDir: '../../../.local/artifacts/admin-browser/tests',
  use: { baseURL: 'https://127.0.0.1:15073', channel: process.env.PORTKILL_TEST_BROWSER_CHANNEL ?? 'chromium', ignoreHTTPSErrors: true, trace: 'off' },
  webServer: [
    { command: 'node scripts/start-test-api.mjs', url: 'https://127.0.0.1:15081/health/live', ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 90000 },
    { command: 'pnpm dev --port 15073', url: 'https://127.0.0.1:15073', ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 30000, env: { PORTKILL_ADMIN_DEV_PROXY: 'https://127.0.0.1:15081', PORTKILL_ADMIN_TEST_CERT_DIRECTORY: fileURLToPath(new URL('../../../.local/artifacts/admin-browser/runtime', import.meta.url)) } },
  ],
});
