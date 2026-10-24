import { expect, test } from '@playwright/test';

test('actual API rejects anonymous access and shell remains usable', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '登录后查看发布记录' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 1, name: '发布概览' })).toBeVisible();
  await page.getByRole('button', { name: '发布历史' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1, name: '发布历史' })).toBeVisible();
  await expect(page.getByRole('button', { name: '发布历史' })).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('.nav-item.active')).toHaveCount(1);
  expect(errors).toEqual([]);
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/light.png', fullPage: true, animations: 'disabled' });
});

test('theme switch changes the working surface', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('switch', { name: '切换深色主题' }).click();
  await expect(page.locator('.workspace')).toHaveClass(/dark/);
  await expect(page.getByRole('heading', { name: '登录后查看发布记录' })).toBeVisible();
  await page.screenshot({ path: '../../../.local/artifacts/admin-browser/dark.png', fullPage: true, animations: 'disabled' });
});

test('narrow viewport has no horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '登录后查看发布记录' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});
