import { expect, test } from '@playwright/test';

test('actual API rejects anonymous access and shell remains usable', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '登录发布管理' })).toBeVisible();
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await expect(page.locator('.breadcrumb')).toHaveCount(0);
  expect(errors).toEqual([]);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/light.png', fullPage: true, animations: 'disabled' });
});

test('theme switch changes the working surface', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('switch', { name: '切换深色主题' }).click();
  await expect(page.locator('.login-shell')).toHaveClass(/dark/);
  await expect(page.getByRole('heading', { name: '登录发布管理' })).toBeVisible();
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/dark.png', fullPage: true, animations: 'disabled' });
});

test('narrow viewport has no horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '登录发布管理' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('skip link moves focus to the main workspace', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: '跳到主内容' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#main-content')).toBeFocused();
});
