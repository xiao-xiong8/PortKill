import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { join } from 'node:path';

const repository = fileURLToPath(new URL('../../../../', import.meta.url));
const local = join(repository, '.local/cache/dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const executable = existsSync(local) ? local : 'dotnet';
const assembly = join(repository, 'tests/PortKiller.UpdateAdmin.BrowserHost/bin/Release/net10.0/PortKiller.UpdateAdmin.BrowserHost.dll');
const runtime = join(repository, '.local/artifacts/admin-browser/runtime');
const build = spawn(executable, ['build', join(repository, 'tests/PortKiller.UpdateAdmin.BrowserHost'), '-c', 'Release', '--nologo'], {
  cwd: repository, stdio: 'inherit', windowsHide: true, env: { ...process.env, PORTKILL_ADMIN_CONFIG: '' },
});
await new Promise((resolve, reject) => {
  build.on('error', reject);
  build.on('exit', (code) => code === 0 ? resolve() : reject(new Error(`Browser host build failed: ${code}`)));
});
const child = spawn(executable, [assembly, runtime], {
  cwd: repository, stdio: 'inherit', windowsHide: true, env: { ...process.env, PORTKILL_ADMIN_CONFIG: '' },
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.on('exit', (code) => process.exit(code ?? 1));
