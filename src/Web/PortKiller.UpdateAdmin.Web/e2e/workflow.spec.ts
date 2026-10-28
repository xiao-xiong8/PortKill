import { createHash, createHmac } from 'node:crypto';
import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readFileSync, existsSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { expect, test, type Page, type BrowserContext } from '@playwright/test';

function totp(key: string, offset = 0) {
  const bytes: number[] = []; let bits = 0, buffer = 0;
  for (const character of key.replaceAll(' ', '')) { buffer = (buffer << 5) | 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(character.toUpperCase()); bits += 5; if (bits >= 8) { bits -= 8; bytes.push((buffer >>> bits) & 255); } }
  const counter = Buffer.alloc(8); counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 30000) + offset));
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  return ((hash.readUInt32BE(hash[19] & 15) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}

test.describe.serial('authenticated administration with real HTTPS API', () => {
  let page: Page;
  let context: BrowserContext;
  let authenticatorKey = '';
  const fixtures = fileURLToPath(new URL('../../../../.local/artifacts/admin-browser/runtime/fixtures/', import.meta.url));
  test.beforeAll(async ({ browser }) => { context = await browser.newContext({ ignoreHTTPSErrors: true }); page = await context.newPage(); });
  test.afterAll(async () => { await context?.close(); });
  test('password, MFA binding, recovery acknowledgement, refresh, recovery login and strong authentication', async () => {
    await page.goto('/');
    await page.getByLabel('账号', { exact: true }).fill('browser-admin');
    await page.getByLabel('密码', { exact: true }).fill('Browser-Test-Only!492');
    await page.getByRole('button', { name: '登录', exact: true }).click();
    await expect(page.getByRole('heading', { name: '绑定验证器' })).toBeVisible();
    await page.getByRole('button', { name: '生成绑定密钥' }).click();
    const key = await page.getByLabel('绑定密钥', { exact: true }).inputValue();
    authenticatorKey = key;
    await page.getByLabel('验证器动态码', { exact: true }).fill(totp(key));
    await page.getByRole('button', { name: '验证并继续' }).click();
    await expect(page.getByRole('heading', { name: '保存恢复码' })).toBeVisible();
    const recovery = (await page.locator('.recovery-codes').innerText()).trim().split('\n');
    await expect(page.getByRole('heading', { name: '当前正式版本' })).toHaveCount(0);
    await page.getByLabel('我已安全保存恢复码').check();
    await page.getByRole('button', { name: '已保存，进入工作空间' }).click();
    await expect(page.getByRole('heading', { name: '当前正式版本' })).toBeVisible();
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
    await expect(page.getByRole('heading', { name: '当前正式版本' })).toBeVisible();
    await page.getByRole('button', { name: '再次认证', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('当前密码').fill('Browser-Test-Only!492');
    await dialog.getByLabel('验证器动态码').fill(totp(authenticatorKey, 1));
    await dialog.getByRole('button', { name: '确认身份' }).click();
    await expect(dialog).toHaveCount(0);
    expect(await page.evaluate(() => Object.keys(localStorage).filter(key => key !== 'portkill.theme'))).toEqual([]);
    expect(await page.evaluate(() => Object.keys(sessionStorage))).toEqual([]);
  });
  test('overview and paginated history use actual empty server responses', async () => {
    await expect(page.getByRole('heading', { name: '尚未发布正式版本' })).toBeVisible();
    await page.getByRole('link', { name: '发布历史', exact: true }).click();
    await expect(page.getByRole('heading', { name: '暂无任务记录' })).toBeVisible();
    await page.getByLabel('任务类型').selectOption('publish');
    await expect(page.getByRole('button', { name: '下一页' })).toBeDisabled();
    await expect(page.locator('.pagination')).toContainText('共 0 条');
  });
  test('client versions show latest reports, filter and revoke without online status', async () => {
    const registration = await context.request.post('/api/v1/client-installations/register', {
      data: { installationId: 'browser-client-installation-001', version: '1.2.3', machineName: 'BROWSER-TEST-PC' },
    });
    expect(registration.status()).toBe(201);
    const credential = await registration.json() as { clientToken: string };
    expect((await context.request.post('/api/v1/client-installations/report', {
      headers: { Authorization: `Bearer ${credential.clientToken}` },
      data: { version: '1.2.4', machineName: 'BROWSER-TEST-PC', result: 'installed' },
    })).status()).toBe(204);
    await page.getByRole('link', { name: '客户端版本', exact: true }).click();
    const table = page.getByRole('table', { name: '客户端最近一次上报' });
    await expect(table.getByRole('cell', { name: 'BROWSER-TEST-PC', exact: true })).toBeVisible();
    await expect(table.getByRole('cell', { name: '1.2.4.0', exact: true })).toBeVisible();
    await expect(table).toContainText('安装成功');
    await expect(table).toContainText('brow…-001');
    await expect(page.locator('#main-content')).not.toContainText('在线');
    await page.getByLabel('客户端版本', { exact: true }).fill('9.9');
    await page.getByRole('button', { name: '筛选', exact: true }).click();
    await expect(page.getByText('暂无符合条件的最近上报记录。')).toBeVisible();
    await page.getByRole('button', { name: '清空', exact: true }).click();
    await expect(table).toBeVisible();
    await page.screenshot({ path: '../../../.local/artifacts/admin-browser/client-versions.png', fullPage: true, animations: 'disabled' });
    await table.getByRole('button', { name: '撤销凭据', exact: true }).click();
    await page.getByRole('button', { name: '确认撤销', exact: true }).click();
    await expect(table.getByRole('cell', { name: '已撤销', exact: true })).toBeVisible();
    expect((await context.request.post('/api/v1/client-installations/report', {
      headers: { Authorization: `Bearer ${credential.clientToken}` },
      data: { version: '1.2.4', machineName: 'BROWSER-TEST-PC', result: 'started' },
    })).status()).toBe(401);
  });
  test('tampered signed package is rejected by the production validation worker', async () => {
    await page.getByRole('link', { name: '上传版本', exact: true }).click();
    await page.getByLabel('签名清单（manifest.json）', { exact: true }).setInputFiles(fixtures + 'manifest.json');
    await page.getByLabel('四文件更新包（ZIP）', { exact: true }).setInputFiles(fixtures + 'tampered.zip');
    await page.getByRole('button', { name: '上传并校验' }).click();
    await expect(page).toHaveURL(/\/job\//);
    await expect(async () => { await page.getByRole('button', { name: '刷新任务' }).click(); await expect(page.locator('.status-label').first()).toHaveText('校验拒绝'); }).toPass({ timeout: 20000, intervals: [500, 1000] });
  });
  test('actual byte transfer can stop, reload, resume an unconfirmed file and validate literal signed notes', async () => {
    test.setTimeout(60000);
    await page.getByRole('link', { name: '上传版本', exact: true }).click();
    const network = await page.context().newCDPSession(page);
    await network.send('Network.enable');
    // Budget the real transfer for eight seconds at either fixture size, so cancellation happens mid-file.
    await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: Math.max(8192, Math.floor(statSync(fixtures + 'package.zip').size / 8)) });
    await page.getByLabel('签名清单（manifest.json）', { exact: true }).setInputFiles(fixtures + 'manifest.json');
    await page.getByLabel('四文件更新包（ZIP）', { exact: true }).setInputFiles(fixtures + 'package.zip');
    await page.getByRole('button', { name: '上传并校验' }).click();
    await expect(page).toHaveURL(/\/upload\//);
    await expect.poll(() => page.getByRole('progressbar', { name: '更新包传输进度' }).getAttribute('value')).not.toBe('0');
    await page.getByRole('button', { name: '停止传输' }).click();
    await expect(page.getByRole('button', { name: '上传并校验' })).toBeEnabled();
    await page.reload();
    await expect(page.getByText('已恢复上传记录，请重新选择原文件')).toBeVisible();
    await page.getByLabel('签名清单（manifest.json）', { exact: true }).setInputFiles(fixtures + 'manifest.json');
    await page.getByLabel('四文件更新包（ZIP）', { exact: true }).setInputFiles(fixtures + 'package.zip');
    await page.getByRole('button', { name: '上传并校验' }).click();
    await expect(page).toHaveURL(/\/job\//, { timeout: 30000 });
    await expect(async () => { await page.getByRole('button', { name: '刷新任务' }).click(); await expect(page.locator('.status-label').first()).toHaveText('可确认发布'); }).toPass({ timeout: 20000, intervals: [500, 1000] });
    await expect(page.locator('.log-lines')).toContainText('<img src=x onerror=window.__releaseXss=1>');
    expect(await page.locator('.log-lines img').count()).toBe(0);
    await network.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: 104857600, uploadThroughput: 104857600 });
    await network.detach();
  });
  test('publish confirmation is one-time and failed public verification can be retried', async () => {
    test.setTimeout(90000);
    const distributionFailure = fileURLToPath(new URL('../../../../.local/artifacts/admin-browser/runtime/distribution-unavailable', import.meta.url));
    writeFileSync(distributionFailure, 'test outage');
    try {
      await expect(page.getByRole('button', { name: '生成发布确认' })).toBeVisible();
      await page.getByRole('button', { name: '生成发布确认' }).click();
      await expect(page.getByText('当前正式清单', { exact: true })).toBeVisible();
      let idempotencyKey = '';
      let acceptedJobId = '';
      await page.route('**/api/v1/releases/*/publish', async route => {
        idempotencyKey = route.request().postDataJSON().idempotencyKey;
        const response = await route.fetch();
        acceptedJobId = (await response.json()).id; // The API accepted the request; only the browser response is lost.
        await route.abort('failed');
      }, { times: 1 });
      await page.getByRole('button', { name: '确认并创建发布任务' }).click();
      await expect(page.getByRole('alert')).toContainText('连接中断');
      const beforeReload = await page.evaluate(async key => {
        const response = await fetch(`/api/v1/publish-requests/${encodeURIComponent(key)}`, { credentials: 'same-origin' });
        return response.json();
      }, idempotencyKey);
      expect(beforeReload.id).toBe(acceptedJobId);
      await page.reload();
      await expect(page.getByRole('button', { name: '查询确认结果' })).toBeVisible();
      await page.getByRole('button', { name: '重试同一确认请求' }).click();
      await expect(page).toHaveURL(new RegExp(`/job/${acceptedJobId}`));
      const recovered = await page.evaluate(async key => {
        const response = await fetch(`/api/v1/publish-requests/${encodeURIComponent(key)}`, { credentials: 'same-origin' });
        return response.json();
      }, idempotencyKey);
      expect(recovered.id).toBe(acceptedJobId);
      expect(decodeURIComponent(new URL(page.url()).pathname.slice('/job/'.length))).toBe(recovered.id);
      await expect(page.locator('.status-label').first()).toHaveText('已生效，下载检查未通过', { timeout: 30000 });
    } finally {
      try { unlinkSync(distributionFailure); } catch { /* The fixture process may already have cleaned it. */ }
    }
    const publishJobs = await page.evaluate(async () => {
      const response = await fetch('/api/v1/jobs?page=1&pageSize=20&kind=publish', { credentials: 'same-origin' });
      return (await response.json()).items;
    });
    expect(publishJobs).toHaveLength(1);
    await page.getByRole('button', { name: '重试下载检查' }).click();
    await expect(page.locator('.status-label').first()).toHaveText('发布完成', { timeout: 30000 });
  });
  test('published bytes install or roll back in Native AOT clients and real reports appear in the web', async () => {
    test.setTimeout(120000);
    const runtime = fileURLToPath(new URL('../../../../.local/artifacts/admin-browser/runtime/', import.meta.url));
    const info = JSON.parse(readFileSync(join(runtime, 'fixture-info.json'), 'utf8')) as {
      clientIntegration?: { OldVersion: string; NewVersion: string; SuccessSandbox: string; RollbackSandbox: string };
    };
    test.skip(!info.clientIntegration, 'Set both PORTKILL_BROWSER_*_PROBE_DIRECTORY values for real installation integration.');
    const client = info.clientIntegration!;
    await page.getByRole('link', { name: '上传版本', exact: true }).click();
    await page.getByLabel('签名清单（manifest.json）', { exact: true }).setInputFiles(fixtures + 'client-manifest.json');
    await page.getByLabel('四文件更新包（ZIP）', { exact: true }).setInputFiles(fixtures + 'client-package.zip');
    await page.getByRole('button', { name: '上传并校验' }).click();
    await expect(page).toHaveURL(/\/job\//);
    await expect(page.locator('.status-label').first()).toHaveText('可确认发布', { timeout: 30000 });
    await page.getByRole('button', { name: '生成发布确认' }).click();
    await page.getByRole('button', { name: '确认并创建发布任务' }).click();
    await expect(page.locator('.status-label').first()).toHaveText('发布完成', { timeout: 30000 });

    type Report = { revocationId: string; installationId: string; clientVersion: string; lastReportResult: string };
    async function reports(): Promise<Report[]> {
      const response = await context.request.get('/api/v1/client-installations?pageSize=100');
      expect(response.status()).toBe(200);
      return ((await response.json()) as { items: Report[] }).items;
    }
    const hash = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex');
    const evidence: object[] = [];
    for (const [sandbox, result] of [[client.SuccessSandbox, 'installed'], [client.RollbackSandbox, 'rolled_back']]) {
      const install = join(sandbox, 'install');
      const binaries = ['PortKiller.exe', 'av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll'];
      const before = binaries.map(name => hash(join(install, name)));
      const settingsHash = hash(join(install, 'update-settings.json'));
      const known = new Set((await reports()).map(item => item.revocationId));
      async function run(...args: string[]) {
        await promisify(execFile)(join(install, 'PortKiller.exe'), args, {
          cwd: install, timeout: 90000, windowsHide: true,
          env: { ...process.env, PORTKILLER_PROBE_ROOT: sandbox,
            PORTKILLER_PROBE_REPORT_CERTIFICATE: join(runtime, 'certificate.pem') },
        });
      }
      await run();
      const initial = (await reports()).find(item => !known.has(item.revocationId));
      expect(initial?.clientVersion).toBe(client.OldVersion);
      expect(initial?.lastReportResult).toBe('started');
      await run('--check-and-install');
      const expectedVersion = result === 'installed' ? client.NewVersion : client.OldVersion;
      await expect.poll(async () => (await reports()).find(item => item.revocationId === initial!.revocationId), { timeout: 60000 })
        .toMatchObject({ clientVersion: expectedVersion, lastReportResult: result, installationId: initial!.installationId });
      await expect.poll(() => readFileSync(join(sandbox, 'launched-version'), 'utf8').split('.').map(Number).concat([0, 0]).slice(0, 4).join('.')).toBe(expectedVersion);
      expect(hash(join(install, 'update-settings.json'))).toBe(settingsHash);
      expect(readFileSync(join(install, 'user-settings.txt'), 'utf8')).toBe('preserved-settings');
      expect(existsSync(join(install, '.portkiller-update', 'transaction.json'))).toBe(false);
      const after = binaries.map(name => hash(join(install, name)));
      if (result === 'rolled_back') expect(after).toEqual(before);
      else {
        const published = process.env.PORTKILL_BROWSER_NEW_PROBE_DIRECTORY!;
        expect(after).toEqual(binaries.map(name => hash(join(published, name === 'PortKiller.exe' ? 'PortKiller.UpdateProbe.exe' : name))));
        expect(after[0]).not.toBe(before[0]);
      }
      await page.getByRole('link', { name: '客户端版本', exact: true }).click();
      await page.getByRole('button', { name: '刷新', exact: true }).click();
      const row = page.getByRole('table', { name: '客户端最近一次上报' }).getByRole('row').filter({ hasText: initial!.installationId });
      await expect(row).toContainText(expectedVersion);
      await expect(row).toContainText(result === 'installed' ? '安装成功' : '已回滚');
      await expect(row).toContainText(result === 'installed' ? '与正式版本一致' : '低于正式版本');
      evidence.push({ installationId: initial!.installationId, previousVersion: client.OldVersion,
        reportedVersion: expectedVersion, result, settingsPreserved: true, binaryHashes: after });
    }
    await page.screenshot({ path: '../../../.local/artifacts/admin-browser/update-closed-loop.png', fullPage: true, animations: 'disabled' });
    writeFileSync(join(runtime, 'client-integration-result.json'), JSON.stringify(evidence, null, 2));
  });
  test('security administration protects account actions and audit data by permission', async () => {
    test.setTimeout(60000);
    async function completeStrongIfShown() {
      const dialog = page.getByRole('dialog');
      if (await dialog.count()) {
        await dialog.getByLabel('当前密码').fill('Browser-Test-Only!492');
        await dialog.getByLabel('验证器动态码').fill(totp(authenticatorKey));
        await dialog.getByRole('button', { name: '确认身份' }).click();
        await expect(dialog).toHaveCount(0);
      }
    }

    await page.getByRole('link', { name: '安全管理', exact: true }).click();
    await expect(page.getByRole('heading', { name: '管理员账号' })).toBeVisible();
    await expect(page.getByText('browser-admin', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: '新建账号' }).click();
    await page.getByLabel('账号', { exact: true }).fill('browser-viewer');
    await page.getByLabel('密码', { exact: true }).fill('Browser-Viewer-Only!492');
    await page.getByLabel('确认密码', { exact: true }).fill('Browser-Viewer-Only!492');
    await page.getByRole('button', { name: '创建账号' }).click();
    await completeStrongIfShown();
    await expect(page.locator('tr').filter({ hasText: 'browser-viewer' })).toBeVisible();

    await page.getByRole('link', { name: '审计记录', exact: true }).click();
    await expect(page.getByRole('heading', { name: '安全操作记录', level: 2 })).toBeVisible();
    await page.getByLabel('操作筛选').fill('account.created');
    await page.getByRole('button', { name: '筛选', exact: true }).click();
    await expect(page.locator('table')).toContainText('account.created');

    const browser = page.context().browser();
    if (!browser) throw new Error('Browser fixture unavailable.');
    const viewerContext = await browser.newContext({ ignoreHTTPSErrors: true });
    const viewerPage = await viewerContext.newPage();
    try {
      await viewerPage.goto('/');
      await viewerPage.getByLabel('账号', { exact: true }).fill('browser-viewer');
      await viewerPage.getByLabel('密码', { exact: true }).fill('Browser-Viewer-Only!492');
      await viewerPage.getByRole('button', { name: '登录', exact: true }).click();
      await expect(viewerPage.getByRole('heading', { name: '绑定验证器' })).toBeVisible();
      await viewerPage.getByRole('button', { name: '生成绑定密钥' }).click();
      const viewerKey = await viewerPage.getByLabel('绑定密钥', { exact: true }).inputValue();
      await viewerPage.getByLabel('验证器动态码', { exact: true }).fill(totp(viewerKey));
      await viewerPage.getByRole('button', { name: '验证并继续' }).click();
      await expect(viewerPage.getByRole('heading', { name: '保存恢复码' })).toBeVisible();
      await viewerPage.getByLabel('我已安全保存恢复码').check();
      await viewerPage.getByRole('button', { name: '已保存，进入工作空间' }).click();
      await expect(viewerPage.getByRole('heading', { name: '当前正式版本' })).toBeVisible();
      await expect(viewerPage.getByRole('link', { name: '上传版本', exact: true })).toHaveCount(0);
      await expect(viewerPage.getByRole('link', { name: '审计记录', exact: true })).toHaveCount(0);
      const denied = await viewerPage.evaluate(async () => (await fetch('/api/v1/accounts?page=1&pageSize=20', { credentials: 'same-origin' })).status);
      expect(denied).toBe(403);
      await viewerPage.goto('/#audit');
      await expect(viewerPage.getByRole('heading', { name: '无权访问此页面' })).toBeVisible();
    } finally {
      await viewerContext.close();
    }

    await page.getByRole('link', { name: '安全管理', exact: true }).click();
    await expect(page.locator('tr').filter({ hasText: 'browser-viewer' })).toBeVisible();
    page.once('dialog', dialog => dialog.accept());
    await page.locator('tr').filter({ hasText: 'browser-viewer' }).getByRole('button', { name: '停用' }).click();
    await completeStrongIfShown();
    await expect(page.locator('tr').filter({ hasText: 'browser-viewer' })).toContainText('已停用');
  });
});
