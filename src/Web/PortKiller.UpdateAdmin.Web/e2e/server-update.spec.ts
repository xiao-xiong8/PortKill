import { expect, test } from '@playwright/test';

test('server update keeps the task through navigation and API restart, then resumes log entries', async ({ page }, testInfo) => {
  let offline = false, finished = false;
  const task = { id: 'a'.repeat(32), packageId: 'b'.repeat(32), version: '1.2.3', state: 'running', stage: '导入应用镜像', updatedUtcMs: Date.now(), terminal: false };
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { userId: 'admin', userName: 'admin', currentSessionId: 'session', phase: 'full', roles: ['Administrator'],
      permissions: ['release.read', 'server.read', 'server.update'], expiresUtcMs: Date.now() + 86400_000, strongAuthenticationExpiresUtcMs: Date.now() + 3600_000 } });
    if (path.startsWith('/api/v1/server-updates/') && offline) return route.abort('connectionrefused');
    if (path === '/api/v1/server-updates/') return route.fulfill({ json: { enabled: true, maximumPackageBytes: 2 * 1024 ** 3, currentVersion: '1.0.0', packages: [],
      tasks: [{ ...task, state: finished ? 'succeeded' : 'running', terminal: finished, stage: finished ? '更新完成' : task.stage }] } });
    if (path.endsWith('/logs')) return route.fulfill({ json: Number(url.searchParams.get('after')) === 0 ? [{ seq: 1, atUtcMs: Date.now(), message: '镜像导入已开始' }]
      : finished ? [{ seq: 2, atUtcMs: Date.now(), message: '健康检查通过' }] : [] });
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/server-update');
  await expect(page.getByRole('heading', { name: '服务器更新', exact: true }).first()).toBeVisible();
  await expect(page.locator('.server-log')).toContainText('镜像导入已开始');
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  await page.getByRole('link', { name: '服务器更新', exact: true }).click();
  await expect(page.locator('.server-log')).toContainText('镜像导入已开始');
  offline = true; await expect(page.getByRole('status').filter({ hasText: '连接中断' })).toBeVisible({ timeout: 10000 });
  await expect(page.locator('.server-task h3')).toHaveText('导入应用镜像');
  offline = false; finished = true;
  await expect(page.locator('.server-task h3')).toHaveText('更新完成', { timeout: 10000 });
  await expect(page.locator('.server-log')).toContainText('健康检查通过');
  await expect(page.getByRole('button', { name: '回退到更新前版本' })).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.screenshot({ path: testInfo.outputPath('server-update-light.png') });
  await page.getByRole('switch', { name: '切换深色主题' }).click();
  await page.screenshot({ path: testInfo.outputPath('server-update-dark.png') });
  await page.setViewportSize({ width: 375, height: 812 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('server ZIP uses CSRF and continues uploading while its page is deactivated', async ({ page }) => {
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  let uploaded = false;
  const packageInfo = { id: 'b'.repeat(32), version: '1.2.3', applicationImage: 'portkill-update-server:test', runnerImage: 'portkill-server-update-runner:test', bytes: 1024, createdUtcMs: Date.now() };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { userId: 'admin', userName: 'admin', currentSessionId: 'session', phase: 'full', roles: ['Administrator'],
      permissions: ['release.read', 'server.read', 'server.update'], expiresUtcMs: Date.now() + 86400_000, strongAuthenticationExpiresUtcMs: Date.now() + 3600_000 } });
    if (path === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'server-upload-csrf' } });
    if (path === '/api/v1/server-updates/') return route.fulfill({ json: { enabled: true, maximumPackageBytes: 10000, currentVersion: '1.0.0', packages: uploaded ? [packageInfo] : [], tasks: [] } });
    if (path === '/api/v1/server-updates/packages') {
      expect(route.request().headers()['x-portkill-csrf']).toBe('server-upload-csrf');
      expect(route.request().headers()['content-type']).toBe('application/octet-stream');
      expect(route.request().postDataBuffer()?.length).toBe(1024);
      await gate; uploaded = true; return route.fulfill({ json: packageInfo });
    }
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/server-update');
  const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/v1/server-updates/packages');
  await page.locator('.server-upload input').setInputFiles({ name: 'server.zip', mimeType: 'application/zip', buffer: Buffer.alloc(1024) });
  await started;
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  release();
  await expect.poll(() => uploaded).toBe(true);
  await page.getByRole('link', { name: '服务器更新', exact: true }).click();
  await expect(page.getByRole('cell', { name: '1.2.3 portkill-update-server:test' })).toBeVisible();
  await expect(page.getByRole('button', { name: '一键更新', exact: true })).toBeEnabled();
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('returning to the update page displays a new failure instead of the cached old success', async ({ page }) => {
  let newer = false;
  const old = { id: 'a'.repeat(32), packageId: 'b'.repeat(32), version: '1.0.0', state: 'succeeded', stage: '旧任务更新完成', updatedUtcMs: 1, terminal: true };
  const failed = { ...old, id: 'c'.repeat(32), version: '1.0.1', state: 'failed', stage: '新任务镜像导入失败', error: 'server_update_failed', updatedUtcMs: 2 };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { userId: 'admin', userName: 'admin', currentSessionId: 'session', phase: 'full', roles: ['Administrator'],
      permissions: ['release.read', 'server.read', 'server.update'], expiresUtcMs: Date.now() + 86400_000, strongAuthenticationExpiresUtcMs: Date.now() + 3600_000 } });
    if (path === '/api/v1/server-updates/') return route.fulfill({ json: { enabled: true, maximumPackageBytes: 10000, currentVersion: '1.0.0', packages: [], tasks: newer ? [old, failed] : [old] } });
    if (path.endsWith('/logs')) return route.fulfill({ json: [{ seq: 1, atUtcMs: 1, message: path.includes(failed.id) ? '新任务失败日志' : '旧任务成功日志' }] });
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/server-update');
  await expect(page.locator('.server-log')).toContainText('旧任务成功日志');
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  newer = true;
  await page.getByRole('link', { name: '服务器更新', exact: true }).click();
  await expect(page.locator('.server-task h3')).toHaveText('新任务镜像导入失败');
  await expect(page.locator('.server-log')).toContainText('新任务失败日志');
  await expect(page.locator('.server-log')).not.toContainText('旧任务成功日志');
  await expect(page.getByRole('button', { name: '回退到更新前版本' })).toHaveCount(0);
});
