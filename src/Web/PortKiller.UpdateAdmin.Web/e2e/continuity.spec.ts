import { test, expect, type Page } from '@playwright/test';

async function fixture(page: Page) {
  let releasePackage!: () => void;
  let releaseMonitoring!: () => void;
  const state = {
    uploadAborts: 0, created: 0, deleted: 0, completed: 0, permissions: ['release.read', 'release.upload'],
    clients: 0, monitoring: 0, monitorFailures: 0,
    clientVersion: '0.1.3.0', limitCurrent: false, failCurrent: false, holdMonitoring: false,
    releasePackage: () => releasePackage(),
    releaseMonitoring: () => releaseMonitoring(),
    packageGate: new Promise<void>(resolve => { releasePackage = resolve; }),
    monitorGate: new Promise<void>(resolve => { releaseMonitoring = resolve; }),
  };
  await page.addInitScript(() => {
    const original = XMLHttpRequest.prototype.abort;
    (window as unknown as { uploadAborts: number }).uploadAborts = 0;
    XMLHttpRequest.prototype.abort = function () {
      (window as unknown as { uploadAborts: number }).uploadAborts++;
      return original.call(this);
    };
  });
  page.on('requestfailed', request => {
    if (request.url().includes('/system/monitoring')) state.monitorFailures++;
  });
  const session = (manifestReceivedBytes = 0, packageReceivedBytes = 0) => ({
    id: 'diagnostic-upload', state: 'uploading',
    manifestExpectedBytes: 2, packageExpectedBytes: 1024,
    manifestReceivedBytes, packageReceivedBytes, expiresUtcMs: Date.now() + 86400000,
  });
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    const now = Date.now();
    if (path === '/api/v1/auth/me') return route.fulfill({ json: {
      userId: 'diagnostic-user', userName: 'diagnostic-user', phase: 'full',
      currentSessionId: 'diagnostic-session', expiresUtcMs: now + 86400000,
      strongAuthenticationExpiresUtcMs: now + 3600000,
      permissions: state.permissions, roles: [],
    } });
    if (path === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'isolated-diagnostic' } });
    if (path === '/api/v1/releases/current') {
      if (state.failCurrent) return route.fulfill({
        status: 503, json: { code: 'release_source_unavailable', detail: '诊断：正式版本源暂时不可用' },
      });
      if (state.limitCurrent) return route.fulfill({
        status: 429, headers: { 'retry-after': '60' }, json: { code: 'rate_limited', detail: '诊断：版本查询限流' },
      });
      return route.fulfill({ json: { observedUtcMs: now, release: null } });
    }
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0, page: 1, pageSize: 20 } });
    if (path === '/api/v1/client-installations') {
      state.clients++;
      return route.fulfill({ json: { items: [{
        installationId: 'diagnostic-installation-001', revocationId: 'diagnostic-installation-001',
        machineName: 'DIAGNOSTIC-PC', clientVersion: state.clientVersion,
        lastReportResult: 'started', lastReportedUtcMs: now, isRevoked: false,
      }], total: 1, page: 1, pageSize: 20 } });
    }
    if (path === '/api/v1/uploads/' && method === 'POST') {
      state.created++;
      return route.fulfill({ json: session() });
    }
    if (path === '/api/v1/uploads/diagnostic-upload/manifest') return route.fulfill({ json: session(2) });
    if (path === '/api/v1/uploads/diagnostic-upload/package') {
      await state.packageGate;
      return route.fulfill({ json: session(2, 1024) }).catch(() => {});
    }
    if (path === '/api/v1/uploads/diagnostic-upload/complete') {
      state.completed++;
      return route.fulfill({ json: { jobId: 'diagnostic-job' } });
    }
    if (path === '/api/v1/uploads/diagnostic-upload') {
      if (method === 'DELETE') { state.deleted++; return route.fulfill({ status: 204 }); }
      return route.fulfill({ json: session(2) });
    }
    if (path === '/api/v1/system/monitoring') {
      state.monitoring++;
      if (state.holdMonitoring) await state.monitorGate;
      const sampled = Date.now();
      return route.fulfill({ json: {
        status: 'ok', servedAtUtcMs: sampled, alerts: [],
        manifest: { status: 'healthy', version: '0.1.3', daysRemaining: 30 },
        snapshot: {
          schemaVersion: 1, collectorStartedAtUtcMs: sampled - 60000, generatedAtUtcMs: sampled,
          resources: {
            cpu: { observedAtUtcMs: sampled, error: null, percent: 23.5 },
            memory: { observedAtUtcMs: sampled, error: null, totalBytes: 8000000000, usedBytes: 2000000000 },
            network: { observedAtUtcMs: sampled, error: null, interface: 'eth0', rxBytesPerSec: 1000, txBytesPerSec: 2000 },
            disks: [{ label: '数据', observedAtUtcMs: sampled, error: null, totalBytes: 100000000000, freeBytes: 50000000000 }],
          },
          series: [{ timestampUtcMs: sampled, cpuPercent: 23.5, memoryPercent: 25, rxBytesPerSec: 1000, txBytesPerSec: 2000, networkInterface: 'eth0' }],
          docker: { observedAtUtcMs: sampled, error: null, services: [] }, appChecks: [],
        },
      } }).catch(() => {});
    }
    return route.fulfill({ status: 404, json: {} });
  });
  return state;
}

test('upload survives navigation and completing in the background does not redirect', async ({ page }) => {
  const state = await fixture(page);
  await page.goto('/upload');
  await selectFiles(page);
  const started = page.waitForRequest(request => request.url().endsWith('/diagnostic-upload/package'));
  await page.getByRole('button', { name: '上传并校验', exact: true }).click(); await started;
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('发布历史');
  expect(await page.evaluate(() => (window as unknown as { uploadAborts: number }).uploadAborts)).toBe(0);
  await page.getByRole('link', { name: '上传版本', exact: true }).click();
  await expect(page).toHaveURL(/\/upload\/diagnostic-upload$/);
  await expect(page.getByText('diagnostic.zip', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '上传并校验', exact: true })).toBeDisabled();
  await page.getByRole('link', { name: '客户端版本', exact: true }).click();
  state.releasePackage();
  await expect.poll(() => state.completed).toBe(1);
  await expect(page).toHaveURL(/\/clients$/);
  await expect(page.getByRole('link', { name: '查看校验任务', exact: true })).toBeVisible();
  expect(state.created).toBe(1); expect(state.deleted).toBe(0);
  await page.getByRole('link', { name: '上传版本', exact: true }).click();
  await expect(page.getByText('diagnostic.zip', { exact: true })).toHaveCount(0);
  await selectFiles(page);
  await expect(page.getByRole('button', { name: '上传并校验', exact: true })).toBeEnabled();
});

async function selectFiles(page: Page) {
  await page.getByLabel('签名清单（manifest.json）', { exact: true }).setInputFiles({
    name: 'manifest.json', mimeType: 'application/json', buffer: Buffer.from('{}'),
  });
  await page.getByLabel('四文件更新包（ZIP）', { exact: true }).setInputFiles({
    name: 'diagnostic.zip', mimeType: 'application/zip', buffer: Buffer.alloc(1024),
  });
}

test('client versions update automatically without hiding the existing table', async ({ page }, info) => {
  const state = await fixture(page);
  await page.goto('/clients');
  await expect(page.getByRole('cell', { name: '0.1.3.0', exact: true })).toBeVisible();
  await page.clock.install();
  state.clientVersion = '0.1.4.0';
  await page.clock.fastForward(30000);
  await expect.poll(() => state.clients).toBeGreaterThan(1);
  await expect(page.getByRole('cell', { name: '0.1.4.0', exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath('client-list-stale.png'), fullPage: true });
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await expect(page.getByRole('cell', { name: '0.1.4.0', exact: true })).toBeVisible();
  expect(state.clients).toBeGreaterThanOrEqual(2);
});

test('returning to overview refreshes old cache without a false disconnect', async ({ page }, info) => {
  await page.clock.install();
  const state = await fixture(page);
  await page.goto('/overview');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  await page.clock.fastForward(30000);
  state.holdMonitoring = true;
  await page.getByRole('link', { name: '运行总览', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: '与监控接口的连接中断' })).toHaveCount(0);
  await expect(page.getByText('23.5%', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: '正在读取最新监控' })).toBeVisible();
  expect(state.monitorFailures).toBe(0);
  expect(state.monitoring).toBe(2);
  await page.screenshot({ path: info.outputPath('inactive-route-false-disconnect.png'), fullPage: true });
  state.releaseMonitoring();
});

test('shared Retry-After is displayed as rate limiting instead of disconnect', async ({ page }, info) => {
  await page.clock.install();
  const state = await fixture(page);
  state.limitCurrent = true;
  await page.goto('/overview');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  await page.clock.runFor(26000);
  await expect(page.getByRole('alert').filter({ hasText: '与监控接口的连接中断' })).toHaveCount(0);
  await expect(page.getByRole('status').filter({ hasText: '管理接口请求限流' })).toBeVisible();
  await expect(page.getByText('23.5%', { exact: true })).toHaveCount(0);
  expect(state.monitoring).toBe(1);
  expect(state.monitorFailures).toBe(0);
  await page.screenshot({ path: info.outputPath('other-api-rate-limit-false-disconnect.png'), fullPage: true });
});

test('formal-release errors never discard a successful client list', async ({ page }, info) => {
  const state = await fixture(page);
  await page.goto('/clients');
  await expect(page.getByRole('cell', { name: '0.1.3.0', exact: true })).toBeVisible();
  state.failCurrent = true;
  state.clientVersion = '0.1.4.0';
  await page.getByRole('button', { name: '刷新', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('诊断：正式版本源暂时不可用');
  expect(state.clients).toBeGreaterThanOrEqual(2);
  await expect(page.getByRole('cell', { name: '0.1.4.0', exact: true })).toBeVisible();
  await expect(page.getByRole('table', { name: '客户端最近一次上报' })).toBeVisible();
  await page.screenshot({ path: info.outputPath('release-failure-hides-successful-client-list.png'), fullPage: true });
});

test('revoking upload permission stops transport and clears private task state', async ({ page }) => {
  const state = await fixture(page); await page.goto('/upload'); await selectFiles(page);
  const started = page.waitForRequest(request => request.url().endsWith('/diagnostic-upload/package'));
  await page.getByRole('button', { name: '上传并校验', exact: true }).click(); await started;
  state.permissions = ['release.read'];
  await page.getByRole('button', { name: '刷新会话状态', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { uploadAborts: number }).uploadAborts)).toBe(1);
  state.releasePackage();
  await expect(page.getByRole('link', { name: '返回上传任务', exact: true })).toHaveCount(0);
  expect(state.completed).toBe(0);
});
