import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname, now = Date.now();
    if (path === '/api/v1/auth/me') return route.fulfill({ json: { userId: 'menu-reader', userName: 'menu-reader',
      currentSessionId: 'menu-session', phase: 'full', roles: ['Viewer'], permissions: ['release.read'], expiresUtcMs: now + 86400_000 } });
    if (path === '/api/v1/system/monitoring') return route.fulfill({ json: { status: 'unconfigured', servedAtUtcMs: now,
      snapshot: null, manifest: { status: 'unknown', version: null, daysRemaining: null }, alerts: [] } });
    if (path === '/api/v1/releases/current') return route.fulfill({ json: { observedUtcMs: now, release: null } });
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/overview');
});

test('account menu supports keyboard selection and Escape returns focus to the avatar', async ({ page }) => {
  const avatar = page.getByRole('button', { name: '账户菜单', exact: true });
  const menu = page.getByRole('menu', { name: '账户菜单', exact: true });
  const reauth = menu.getByRole('menuitem', { name: '再次认证' }), logout = menu.getByRole('menuitem', { name: '退出', exact: true });
  await expect(avatar).toHaveAttribute('aria-haspopup', 'menu');
  await expect(avatar).toHaveAttribute('aria-controls', 'account-menu-actions');
  await avatar.focus(); await page.keyboard.press('Enter');
  await expect(avatar).toHaveAttribute('aria-expanded', 'true'); await expect(reauth).toBeFocused();
  await page.keyboard.press('ArrowDown'); await expect(logout).toBeFocused();
  await page.keyboard.press('ArrowDown'); await expect(reauth).toBeFocused();
  await page.keyboard.press('ArrowUp'); await expect(logout).toBeFocused();
  await page.keyboard.press('Home'); await expect(reauth).toBeFocused();
  await page.keyboard.press('End'); await expect(logout).toBeFocused();
  await page.keyboard.press('Escape'); await expect(menu).toBeHidden(); await expect(avatar).toBeFocused();
  await expect(avatar).toHaveAttribute('aria-expanded', 'false');
  await page.keyboard.press('ArrowUp'); await expect(logout).toBeFocused();
  await page.keyboard.press('Escape'); await page.keyboard.press('ArrowDown'); await expect(reauth).toBeFocused();
  await page.keyboard.press('Escape'); await page.keyboard.press('Space'); await expect(reauth).toBeFocused();
});

test('Tab exits the menu and outside clicks keep focus on the clicked control', async ({ page }) => {
  const avatar = page.getByRole('button', { name: '账户菜单', exact: true });
  const menu = page.getByRole('menu', { name: '账户菜单', exact: true });
  const theme = page.getByRole('switch', { name: '切换深色主题' });
  await avatar.click(); await page.keyboard.press('Tab');
  await expect(menu).toBeHidden(); await expect(page.getByRole('button', { name: '刷新总览' })).toBeFocused();
  await avatar.click(); await page.keyboard.press('Shift+Tab');
  await expect(menu).toBeHidden(); await expect(theme).toBeFocused();
  await avatar.click(); await theme.click(); await expect(menu).toBeHidden(); await expect(theme).toBeFocused();
  await avatar.click(); await page.getByRole('link', { name: '发布历史', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('发布历史'); await expect(menu).toBeHidden();
});

test('cancelling authentication returns focus to the visible avatar', async ({ page }) => {
  const avatar = page.getByRole('button', { name: '账户菜单', exact: true });
  await avatar.click(); await page.getByRole('menuitem', { name: '再次认证' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('当前密码')).toBeFocused();
  await dialog.getByRole('button', { name: '取消', exact: true }).click();
  await expect(dialog).toHaveCount(0); await expect(avatar).toBeFocused();
  await avatar.click(); await page.getByRole('menuitem', { name: '再次认证' }).click();
  await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0); await expect(avatar).toBeFocused();
});

for (const width of [375, 1280]) {
  for (const dark of [false, true]) {
    test(`account menu stays inside the ${width}px ${dark ? 'dark' : 'light'} viewport`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 812 });
      if (dark) await page.getByRole('switch', { name: '切换深色主题' }).click();
      await page.getByRole('button', { name: '账户菜单', exact: true }).click();
      const menu = page.locator('.account-dropdown');
      await expect(menu).toBeVisible(); await expect(menu.locator('.identity')).toHaveText('menu-reader');
      const bounds = await menu.boundingBox();
      expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('account-menu.png') });
    });
  }
}
