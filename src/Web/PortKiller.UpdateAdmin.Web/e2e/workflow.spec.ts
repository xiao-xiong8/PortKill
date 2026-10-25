import { createHmac } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';

function totp(key: string, offset = 0) {
  const bytes: number[] = []; let bits = 0, buffer = 0;
  for (const character of key.replaceAll(' ', '')) { buffer = (buffer << 5) | 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(character.toUpperCase()); bits += 5; if (bits >= 8) { bits -= 8; bytes.push((buffer >>> bits) & 255); } }
  const counter = Buffer.alloc(8); counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 30000) + offset));
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  return ((hash.readUInt32BE(hash[19] & 15) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}

test.describe.serial('authenticated administration with real HTTPS API', () => {
  let page: Page;
  test.beforeAll(async ({ browser }) => { page = await browser.newPage({ ignoreHTTPSErrors: true }); });
  test.afterAll(async () => { await page.close(); });
  test('password, MFA binding, recovery acknowledgement, refresh, recovery login and strong authentication', async () => {
    await page.goto('/');
    await page.getByLabel('账号', { exact: true }).fill('browser-admin');
    await page.getByLabel('密码', { exact: true }).fill('Browser-Test-Only!492');
    await page.getByRole('button', { name: '登录', exact: true }).click();
    await expect(page.getByRole('heading', { name: '绑定验证器' })).toBeVisible();
    await page.getByRole('button', { name: '生成绑定密钥' }).click();
    const key = await page.getByLabel('绑定密钥', { exact: true }).inputValue();
    await page.getByLabel('验证器动态码', { exact: true }).fill(totp(key));
    await page.getByRole('button', { name: '验证并继续' }).click();
    await expect(page.getByRole('heading', { name: '保存恢复码' })).toBeVisible();
    const recovery = (await page.locator('.recovery-codes').innerText()).trim().split('\n');
    await expect(page.getByRole('heading', { name: '会话已连接' })).toHaveCount(0);
    await page.getByLabel('我已安全保存恢复码').check();
    await page.getByRole('button', { name: '已保存，进入工作空间' }).click();
    await expect(page.getByRole('heading', { name: '会话已连接' })).toBeVisible();
    await page.reload(); await expect(page.locator('.identity')).toHaveText('browser-admin');
    await page.getByRole('button', { name: '退出', exact: true }).click();
    await expect(page.getByRole('heading', { name: '登录发布管理' })).toBeVisible();
    await page.getByLabel('账号', { exact: true }).fill('browser-admin');
    await page.getByLabel('密码', { exact: true }).fill('Browser-Test-Only!492');
    await page.getByRole('button', { name: '登录', exact: true }).click();
    await expect(page.getByRole('heading', { name: '验证登录' })).toBeVisible();
    await page.getByRole('button', { name: '使用恢复码登录' }).click();
    await page.getByLabel('恢复码', { exact: true }).fill(recovery[0]);
    await page.getByRole('button', { name: '验证并继续' }).click();
    await expect(page.getByRole('heading', { name: '会话已连接' })).toBeVisible();
    await page.getByRole('button', { name: '再次认证', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('当前密码').fill('Browser-Test-Only!492');
    await dialog.getByLabel('验证器动态码').fill(totp(key, 1));
    await dialog.getByRole('button', { name: '确认身份' }).click();
    await expect(dialog).toHaveCount(0);
    expect(await page.evaluate(() => Object.keys(localStorage).filter(key => key !== 'portkill.theme'))).toEqual([]);
    expect(await page.evaluate(() => Object.keys(sessionStorage))).toEqual([]);
  });
});
