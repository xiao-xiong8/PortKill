import { expect, test } from '@playwright/test';

test('login artwork follows system and explicit themes, weather and reduced motion', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await page.goto('/login');
  const experience = page.locator('.login-experience');
  const scene = page.locator('#pelican-scene');
  await expect(scene).toBeVisible();
  await expect(page.getByRole('button', { name: '播放动画', exact: true })).toHaveAttribute('aria-pressed', 'true');
  const frame = await page.locator('#pelican-frontFoot').getAttribute('transform');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(experience).toHaveAttribute('data-theme', 'dark');
  await expect(page.locator('#pelican-weatherLabel')).toContainText('晴夜');
  await page.getByRole('button', { name: '浅色模式', exact: true }).click();
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(experience).toHaveAttribute('data-theme', 'light');
  await page.reload();
  await expect(experience).toHaveAttribute('data-theme', 'light');
  await page.getByRole('button', { name: '跟随系统', exact: true }).click();
  await expect(experience).toHaveAttribute('data-theme', 'dark');
  for (const [label, value, count] of [['小雨', 'rain', 82], ['落雪', 'snow', 56], ['多云', 'cloudy', 0], ['自动', 'clear', 0]] as const) {
    await page.getByRole('button', { name: label, exact: true }).click();
    await expect(experience).toHaveAttribute('data-weather', value);
    await expect(page.locator('#pelican-weatherEffects > *')).toHaveCount(count);
  }
  expect(await page.locator('#pelican-frontFoot').getAttribute('transform')).toBe(frame);
  await page.getByRole('button', { name: '播放动画', exact: true }).click();
  await expect.poll(() => page.locator('#pelican-frontFoot').getAttribute('transform')).not.toBe(frame);
  await page.getByRole('button', { name: '暂停动画', exact: true }).click();
  await expect(page.getByRole('button', { name: '播放动画', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('real login form validates, handles errors, sends CSRF, verifies MFA and returns to the requested page', async ({ page }) => {
  let phase: string | null = null;
  const submissions: Record<string, unknown>[] = [];
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/v1/auth/me') return phase ? route.fulfill({ json: {
      userId: 'login-reader', userName: 'login-reader', currentSessionId: 'login-session', phase,
      roles: ['Viewer'], permissions: ['release.read'], expiresUtcMs: Date.now() + 86400_000,
    } }) : route.fulfill({ status: 401, json: { code: 'authentication_required' } });
    if (path === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'login-test-csrf' } });
    if (path === '/api/v1/auth/login') {
      submissions.push(route.request().postDataJSON());
      expect(route.request().headers()['x-portkill-csrf']).toBe('login-test-csrf');
      if (submissions.length === 1) return route.fulfill({ status: 401, json: { code: 'invalid_credentials', detail: '账号或密码不正确' } });
      phase = 'mfaRequired';
      return route.fulfill({ status: 204 });
    }
    if (path === '/api/v1/auth/mfa/verify') {
      expect(route.request().postDataJSON()).toEqual({ code: '123456' });
      phase = 'full';
      return route.fulfill({ status: 204 });
    }
    if (path === '/api/v1/jobs') return route.fulfill({ json: { items: [], total: 0 } });
    return route.fulfill({ status: 404, json: {} });
  });
  await page.goto('/login?returnTo=%2Fhistory%3Ftype%3Dpublish');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByLabel('账号', { exact: true })).toBeFocused();
  await expect(page.getByText('请输入管理员账号', { exact: true })).toBeVisible();
  await expect(page.getByText('请输入登录密码', { exact: true })).toBeVisible();
  expect(submissions).toHaveLength(0);
  await page.getByLabel('账号', { exact: true }).fill(' login-reader ');
  await page.getByLabel('密码', { exact: true }).fill('wrong-test-password');
  await page.getByRole('button', { name: '显示密码', exact: true }).click();
  await expect(page.getByLabel('密码', { exact: true })).toHaveAttribute('type', 'text');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('账号或密码不正确');
  await expect(page.getByLabel('密码', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('密码', { exact: true })).toHaveAttribute('type', 'password');
  expect(submissions[0]).toEqual({ userName: 'login-reader', password: 'wrong-test-password' });
  await page.getByLabel('密码', { exact: true }).fill('valid-test-password');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('heading', { name: '验证登录' })).toBeVisible();
  await page.getByLabel('验证器动态码', { exact: true }).fill('123');
  await page.getByRole('button', { name: '验证并继续' }).click();
  await expect(page.getByText('验证码须为 6 位数字', { exact: true })).toBeVisible();
  await page.getByLabel('验证器动态码', { exact: true }).fill('123456');
  await page.getByRole('button', { name: '验证并继续' }).click();
  await expect(page).toHaveURL(/\/history\?type=publish$/);
  await expect(page.locator('.sidebar')).toBeVisible();
  await expect(page.locator('#pelican-scene')).toHaveCount(0);
  expect(await page.evaluate(() => JSON.stringify(localStorage) + JSON.stringify(sessionStorage))).not.toMatch(/valid-test-password|wrong-test-password|123456/);
});

test('login fits desktop and mobile without horizontal overflow', async ({ page }, testInfo) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/login');
  for (const width of [1920, 1440, 1024, 768, 760, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const form = await page.locator('.auth-panel').boundingBox();
    expect(form!.width).toBeGreaterThanOrEqual(260);
    if (width > 760) {
      const art = await page.locator('.world').boundingBox();
      const right = await page.locator('.login-side').boundingBox();
      expect(art!.width).toBe(width / 2);
      expect(right!.width).toBe(width / 2);
    }
    if (width === 1440 || width === 390) await page.screenshot({ path: testInfo.outputPath(`login-${width}.png`), fullPage: true });
  }
});

test('MFA screens fit a narrow phone and cancelling enrollment clears its secret', async ({ page }) => {
  let phase: string | null = 'enrollmentRequired';
  await page.route('**/api/v1/auth/**', route => {
    const endpoint = new URL(route.request().url()).pathname;
    if (endpoint === '/api/v1/auth/me') return phase ? route.fulfill({ json: {
      userId: 'mfa-reader', userName: 'mfa-reader', currentSessionId: 'mfa-session', phase,
      roles: [], permissions: [], expiresUtcMs: Date.now() + 86400_000,
    } }) : route.fulfill({ status: 401, json: {} });
    if (endpoint === '/api/v1/auth/csrf') return route.fulfill({ json: { requestToken: 'mfa-test-csrf' } });
    if (endpoint === '/api/v1/auth/mfa/enrollment/start') return route.fulfill({ json: { sharedKey: 'ENROLLMENTTESTKEY' } });
    if (endpoint === '/api/v1/auth/logout') { phase = null; return route.fulfill({ status: 204 }); }
    if (endpoint === '/api/v1/auth/login') { phase = 'enrollmentRequired'; return route.fulfill({ status: 204 }); }
    return route.fulfill({ status: 404, json: {} });
  });
  await page.setViewportSize({ width: 320, height: 720 });
  await page.goto('/login');
  await page.getByRole('button', { name: '生成绑定密钥', exact: true }).click();
  await expect(page.getByLabel('绑定密钥', { exact: true })).toHaveValue('ENROLLMENTTESTKEY');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: '返回登录', exact: true }).click();
  await page.getByLabel('账号', { exact: true }).fill('mfa-reader');
  await page.getByLabel('密码', { exact: true }).fill('mfa-test-password');
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('button', { name: '生成绑定密钥', exact: true })).toBeVisible();
  await expect(page.getByLabel('绑定密钥', { exact: true })).toHaveCount(0);
  phase = 'recoveryAcknowledgementRequired';
  await page.reload();
  await expect(page.getByLabel('我已安全保存恢复码')).toBeVisible();
  await page.getByRole('button', { name: '已保存，进入工作空间', exact: true }).click();
  await expect(page.getByText('请勾选确认已安全保存恢复码', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  phase = 'mfaRequired';
  await page.reload();
  await page.getByRole('button', { name: '使用恢复码登录', exact: true }).click();
  await expect(page.getByLabel('恢复码', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
