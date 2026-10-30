import { expect, test, type Page } from '@playwright/test';
import type { components } from '../src/api/contracts.generated';

async function monitoringPage(page: Page) {
  const calls = { monitoring: 0, jobs: 0, auth: 0, fail: false, dockerAge: 0, sampleAge: 0, status: 'ok', cpu: 23.5,
    sessionId: 'monitor-session', authStatus: 200, gap: false, holdAuth: undefined as Promise<void> | undefined,
    holdMonitoring: undefined as Promise<void> | undefined, alerts: [] as components['schemas']['MonitoringAlert'][] };
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url()), now = Date.now();
    if (url.pathname === '/api/v1/auth/me') {
      calls.auth++; await calls.holdAuth;
      return route.fulfill({ status: calls.authStatus, json: { userName: 'monitor-reader', userId: 'monitor-user',
        currentSessionId: calls.sessionId, phase: 'full', expiresUtcMs: now + 86400_000,
        permissions: ['release.read'], strongAuthenticationExpiresUtcMs: now + 60_000 } });
    }
    if (url.pathname === '/api/v1/system/monitoring') {
      calls.monitoring++;
      await calls.holdMonitoring;
      if (calls.fail) return route.fulfill({ status: 503, json: { detail: '监控连接失败' } });
      return route.fulfill({ json: { status: calls.status, servedAtUtcMs: now, alerts: calls.alerts, manifest: { status: 'healthy', version: '1.2.3', daysRemaining: 30 },
        snapshot: { schemaVersion: 1, collectorStartedAtUtcMs: now - 900_000, generatedAtUtcMs: now - calls.sampleAge,
          resources: { cpu: { observedAtUtcMs: now - calls.sampleAge, error: null, percent: calls.cpu },
            memory: { observedAtUtcMs: now, error: null, totalBytes: 8_000_000_000, usedBytes: 2_000_000_000 },
            network: { observedAtUtcMs: now, error: null, interface: 'ens3', rxBytesPerSec: 1000, txBytesPerSec: 2000 },
            disks: [{ label: '管理数据 / 更新包', observedAtUtcMs: now, error: null, totalBytes: 100_000_000_000, freeBytes: 50_000_000_000 }] },
          series: Array.from({ length: 181 }, (_, index) => ({ timestampUtcMs: now - 900_000 + index * 5000,
            cpuPercent: calls.cpu, memoryPercent: 25, rxBytesPerSec: 1000, txBytesPerSec: 2000, networkInterface: 'ens3' }))
            .filter((_, index) => !calls.gap || index < 20 || index > 160),
          docker: { observedAtUtcMs: now - calls.dockerAge, error: null, services: ['api', 'worker', 'notifier', 'caddy'].map(service => ({ service, state: service === 'worker' && calls.alerts.some(alert => alert.code === 'container_state') ? 'exited' : 'running', health: 'not_configured', restartCount: 0, containerId: 'abc' })) },
          appChecks: ['api', 'notifier', 'download'].map(name => ({ name, observedAtUtcMs: now, error: null, status: 'healthy', manifestValid: true })) } } });
    }
    if (url.pathname === '/api/v1/releases/current') return route.fulfill({ json: { observedUtcMs: now, release: null } });
    if (url.pathname === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'local-test-token' } });
    if (url.pathname === '/api/v1/auth/login') return route.fulfill({ json: {} });
    if (url.pathname === '/api/v1/jobs') { calls.jobs++; return route.fulfill({ json: { items: [], total: 0 } }); }
    return route.fulfill({ status: 404, json: {} });
  });
  return calls;
}

test('overview shows host metrics and keeps normal navigation with one h1', async ({ page }) => {
  await monitoringPage(page);
  await page.goto('/overview');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('运行总览');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  await expect(page.getByText('未检查（未配置业务探针）')).toBeVisible();
  await expect(page.getByRole('heading', { name: '最近任务' })).toBeVisible();
  await expect(page.locator('tbody tr')).toHaveCount(4);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-light.png', fullPage: true });
  await page.getByRole('switch', { name: '切换深色主题' }).click();
  await expect(page.locator('.workspace')).toHaveClass(/dark/);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-dark.png', fullPage: true });
  await page.setViewportSize({ width: 1920, height: 1080 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-desktop-wide.png', fullPage: true });
});

test('screen login return, fullscreen fallback, refresh and narrow layout remain usable', async ({ page }) => {
  await page.clock.install();
  const calls = await monitoringPage(page);
  await page.addInitScript(() => { document.documentElement.requestFullscreen = async () => { throw new Error('denied'); }; });
  await page.goto('/login?returnTo=' + encodeURIComponent('/overview?display=screen'));
  await expect(page).toHaveURL(/\/overview\?display=screen$/);
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await expect(page.locator('.page-footer')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: '最近任务' })).toHaveCount(0);
  await page.getByRole('button', { name: '账户菜单' }).click();
  await expect(page.getByRole('menuitem', { name: '退出', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '账户菜单' }).click();
  expect(calls.jobs).toBe(0);
  await page.clock.fastForward(1100);
  await page.getByRole('button', { name: '刷新总览' }).click();
  await expect.poll(() => calls.monitoring).toBe(2);
  await page.getByRole('button', { name: '退出大屏' }).click();
  await expect(page.locator('.sidebar')).toBeVisible();
  await page.getByRole('button', { name: '进入大屏' }).click();
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await page.setViewportSize({ width: 375, height: 812 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-screen-narrow.png', fullPage: true });
});

test('failed refresh preserves the page but old observations turn unknown', async ({ page }) => {
  const calls = await monitoringPage(page);
  await page.clock.install();
  await page.goto('/overview');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  calls.fail = true;
  await page.clock.runFor(25_000);
  await expect(page.getByText(/与监控接口的连接中断/)).toBeVisible();
  await expect(page.getByText('采集已断流', { exact: true })).toHaveCount(0);
  await expect(page.getByText('23.5%', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: '当前正式版本' })).toBeVisible();
  await expect(page.locator('tbody tr').first()).toContainText('运行中');
  await page.clock.fastForward(20_000);
  await expect(page.locator('tbody tr').first()).toContainText('未知');
});

test('login return does not accept an external destination', async ({ page }) => {
  await monitoringPage(page);
  await page.goto('/login?returnTo=' + encodeURIComponent('https://example.org/overview?display=screen'));
  await expect(page).toHaveURL(/\/overview$/);
});

test('disconnect moves last alerts into dated history and successful refresh clears them', async ({ page }) => {
  await page.clock.install();
  const calls = await monitoringPage(page);
  calls.alerts = [{ code: 'manifest_expiry', source: 'manifest', severity: 'critical', message: '正式清单即将到期。' }];
  await page.goto('/overview');
  await expect(page.getByRole('region', { name: '当前异常', exact: true })).toContainText('正式清单即将到期。');
  calls.fail = true; await page.clock.fastForward(25_000);
  await expect(page.getByRole('region', { name: '当前异常', exact: true })).toHaveCount(0);
  const history = page.getByRole('region', { name: '历史异常', exact: true });
  await expect(history).toContainText('最近一次异常（数据已过期）');
  await expect(history).toContainText('正式清单即将到期。');
  await expect(history).toContainText(/上次观察 \d{4}/);
  calls.fail = false; calls.alerts = [];
  await page.clock.fastForward(31_000);
  await page.getByRole('button', { name: '刷新总览' }).click();
  await expect(history).toHaveCount(0);
  await expect(page.getByText('正式清单即将到期。', { exact: true })).toHaveCount(0);
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
});

test('navigation and overview cache do not wait for slow background endpoints', async ({ page }, testInfo) => {
  await page.clock.install();
  const calls = await monitoringPage(page); await page.goto('/overview');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  let releaseAuth!: () => void;
  calls.holdAuth = new Promise(resolve => { releaseAuth = resolve; });
  await page.clock.fastForward(30_001);
  await expect.poll(() => calls.auth).toBe(2);
  const started = Date.now();
  await page.getByRole('link', { name: '发布历史', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('发布历史', { timeout: 1000 });
  const historyMs = Date.now() - started;
  releaseAuth(); calls.holdAuth = undefined;
  await page.clock.fastForward(5001);
  let releaseMonitoring!: () => void;
  calls.holdMonitoring = new Promise(resolve => { releaseMonitoring = resolve; }); calls.cpu = 33.3;
  const returnStarted = Date.now();
  await page.getByRole('link', { name: '运行总览', exact: true }).click();
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible({ timeout: 1000 });
  await testInfo.attach('navigation-timings', { body: JSON.stringify({ historyMs, cachedOverviewMs: Date.now() - returnStarted }), contentType: 'application/json' });
  releaseMonitoring(); calls.holdMonitoring = undefined;
  await expect(page.getByText('33.3%', { exact: true })).toBeVisible();
});

test('a delayed healthy sample stays distinct from a server-confirmed stopped collector', async ({ page }) => {
  await page.clock.install();
  const calls = await monitoringPage(page); calls.sampleAge = 4999;
  await page.goto('/overview'); await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  await page.clock.fastForward(16_000);
  await expect(page.getByText('采集已断流', { exact: true })).toHaveCount(0);
  calls.status = 'stale'; calls.sampleAge = 21_000;
  await page.clock.fastForward(5000);
  await expect(page.getByText(/采集器快照已过期，上次采样/)).toBeVisible();
  await expect(page.getByText('23.5%', { exact: true })).toHaveCount(0);
  await expect(page.locator('tbody tr').first()).toContainText('运行中');
});

test('charts render numeric axes and show actual samples on hover and keyboard', async ({ page }) => {
  await monitoringPage(page); await page.goto('/overview');
  const chart = page.locator('.monitor-chart').first();
  await expect(chart.locator('svg text').filter({ hasText: '100%' })).toBeVisible();
  await expect(chart.locator('svg text').filter({ hasText: '50%' })).toBeVisible();
  await expect(chart.locator('svg text').filter({ hasText: /\d{2}:\d{2}/ }).first()).toBeVisible();
  const graph = await chart.locator('.resource-echart').boundingBox();
  await page.mouse.move(graph!.x + graph!.width / 2, graph!.y + 90);
  await expect(chart.locator('.monitor-tooltip')).toBeVisible();
  await expect(chart.locator('.monitor-tooltip')).toContainText('CPU 23.5% · 内存 25.0%');
  await expect(chart.locator('.chart-readout')).toContainText('CPU 23.5%');
  await chart.locator('.monitor-chart-control').focus(); await page.keyboard.press('ArrowLeft');
  await expect(chart.locator('.chart-readout')).toContainText('内存 25.0%');
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-echarts-tooltip.png', fullPage: true });
  await page.keyboard.press('Escape');
  await expect(chart.locator('.chart-readout')).toContainText('移动鼠标');
  const network = page.locator('.monitor-chart').nth(1);
  await network.locator('.monitor-chart-control').focus(); await page.keyboard.press('ArrowLeft');
  await expect(network.locator('.chart-readout')).toContainText('接收 1000 B/s · 发送 2.0 KiB/s');
  await expect(network.locator('.chart-readout')).toContainText('网卡 ens3');
});

test('hovering a real sample gap does not show interpolated or distant values', async ({ page }) => {
  const calls = await monitoringPage(page); calls.gap = true;
  await page.goto('/overview'); const chart = page.locator('.monitor-chart').first();
  await expect(chart.locator('svg text').filter({ hasText: '100%' })).toBeVisible();
  const graph = await chart.locator('.resource-echart').boundingBox();
  await page.mouse.move(graph!.x + graph!.width / 2, graph!.y + 90);
  await expect(chart.locator('.chart-readout')).toContainText('该时段无采样');
  const tooltip = chart.locator('.monitor-tooltip');
  await expect(tooltip).toBeVisible(); await expect(tooltip).toContainText('该时段无采样');
});

test.describe('touch monitoring chart', () => {
  test.use({ hasTouch: true, viewport: { width: 375, height: 812 } });
  test('a narrow chart supports touch selection without overflowing the viewport', async ({ page }) => {
    await monitoringPage(page); await page.goto('/overview?display=screen');
    const chart = page.locator('.monitor-chart').first();
    await expect(chart.locator('svg text').filter({ hasText: '100%' })).toBeVisible();
    const graph = chart.locator('.resource-echart'); await graph.scrollIntoViewIfNeeded();
    const box = await graph.boundingBox(); await graph.tap({ position: { x: box!.width / 2, y: 90 } });
    await expect(chart.locator('.monitor-tooltip')).toBeVisible();
    await expect(chart.locator('.monitor-tooltip')).toContainText('CPU 23.5%');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: '../../../.local/artifacts/admin-browser/monitoring-touch-narrow.png' });
  });
});

test('expired authentication clears the overview cache before a different session returns', async ({ page }) => {
  await page.clock.install();
  const calls = await monitoringPage(page); await page.goto('/overview');
  await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
  calls.authStatus = 401;
  await page.getByRole('button', { name: '刷新会话状态' }).click(); await expect(page).toHaveURL(/\/login/);
  calls.authStatus = 200; calls.sessionId = 'another-session'; calls.cpu = 88.8;
  let release!: () => void; calls.holdMonitoring = new Promise(resolve => { release = resolve; });
  await page.getByLabel('账号', { exact: true }).fill('another-reader');
  await page.getByLabel('密码', { exact: true }).fill('local-test-password');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('运行总览');
  await expect(page.getByText('23.5%', { exact: true })).toHaveCount(0);
  release(); calls.holdMonitoring = undefined;
  await expect(page.getByText('88.8%', { exact: true })).toBeVisible();
});

test('only the expired source alert moves to history and fresh observation restores it', async ({ page }) => {
  await page.clock.install();
  const calls = await monitoringPage(page);
  calls.dockerAge = 44_000;
  calls.alerts = [{ code: 'container_state', source: 'worker', severity: 'critical', message: 'worker 容器状态异常。' },
    { code: 'manifest_expiry', source: 'manifest', severity: 'critical', message: '正式清单即将到期。' }];
  await page.goto('/overview');
  const current = page.getByRole('region', { name: '当前异常', exact: true });
  const history = page.getByRole('region', { name: '历史异常', exact: true });
  await expect(current).toContainText('worker 容器状态异常。');
  await page.clock.fastForward(2000);
  await expect(current).not.toContainText('worker 容器状态异常。');
  await expect(current).toContainText('正式清单即将到期。');
  await expect(history).toContainText('worker 容器状态异常。');
  calls.dockerAge = 0; await page.getByRole('button', { name: '刷新总览' }).click();
  await expect(current).toContainText('worker 容器状态异常。');
  await expect(history).toHaveCount(0);
});

for (const theme of ['light', 'dark']) {
  test(`both chart legends have readable contrast in ${theme} theme`, async ({ page }, testInfo) => {
    await page.addInitScript(theme => localStorage.setItem('portkill.theme', theme), theme);
    await monitoringPage(page); await page.goto('/overview');
    await expect(page.getByText('23.5%', { exact: true })).toBeVisible();
    const colors = await page.locator('.monitor-chart').first().evaluate(chart => ({
      background: getComputedStyle(chart).backgroundColor,
      foregrounds: [...chart.querySelectorAll('.chart-legend .chart-first, .chart-legend .chart-second')].map(item => getComputedStyle(item).color),
    }));
    const luminance = (color: string) => {
      const channels = color.match(/\d+/g)!.slice(0, 3).map(value => Number(value) / 255)
        .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
      return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    };
    const background = luminance(colors.background);
    const ratios = colors.foregrounds.map(color => {
      const foreground = luminance(color);
      return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
    });
    await testInfo.attach('chart-legend-contrast', { body: JSON.stringify({ theme, ...colors, ratios }, null, 2), contentType: 'application/json' });
    expect(ratios).toHaveLength(2);
    for (const ratio of ratios) expect(ratio).toBeGreaterThanOrEqual(4.5);
  });
}
