import { expect, test } from '@playwright/test';

test('login honors Retry-After and enables resubmission when the countdown ends', async ({ page }) => {
  let submissions = 0;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'test-csrf' } });
    if (path === '/api/v1/auth/login') {
      submissions++;
      return route.fulfill({ status: 429, headers: { 'retry-after': '3' }, json: { code: 'rate_limited', detail: '请求过于频繁' } });
    }
    return route.fulfill({ status: 401, json: { code: 'authentication_required' } });
  });
  await page.goto('/login');
  await page.getByLabel('账号', { exact: true }).fill('test-user');
  await page.getByLabel('密码', { exact: true }).fill('test-password');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('button', { name: '登录', exact: true })).toBeDisabled();
  await expect(page.getByRole('status').filter({ hasText: /秒后重试/ })).toBeVisible();
  await expect(page.getByRole('button', { name: '登录', exact: true })).toBeEnabled({ timeout: 6000 });
  expect(submissions).toBe(1);
});

test('logout in one tab clears protected content in another tab sharing the cookie', async ({ context, page }) => {
  let authenticated = true;
  await context.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'test-csrf' } });
    if (path === '/api/v1/auth/logout') { authenticated = false; return route.fulfill({ status: 204 }); }
    if (path === '/api/v1/auth/me') return authenticated ? route.fulfill({ json: {
      userId: 'test', userName: 'test', currentSessionId: 'shared-cookie', phase: 'full', roles: [],
      permissions: ['release.read'], expiresUtcMs: Date.now() + 3600000,
    } }) : route.fulfill({ status: 401, json: { code: 'authentication_required' } });
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ json: { release: null } });
  });
  await page.goto('/history');
  const second = await context.newPage(); await second.goto('/history');
  await expect(second.locator('.sidebar')).toBeVisible();
  await page.getByRole('button', { name: '账户菜单' }).click();
  await page.getByRole('menuitem', { name: '退出', exact: true }).click();
  await expect(second).toHaveURL(/\/login/);
  await expect(second.locator('.sidebar')).toHaveCount(0);
  await expect(second.getByRole('heading', { name: '登录发布管理' })).toBeVisible();
});
