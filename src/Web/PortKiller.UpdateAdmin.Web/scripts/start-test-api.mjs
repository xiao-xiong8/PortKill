import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const repository = fileURLToPath(new URL('../../../../', import.meta.url));
const local = join(repository, '.local/cache/dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const executable = existsSync(local) ? local : 'dotnet';
const assembly = join(repository, 'src/Services/PortKiller.UpdateAdmin.Api/bin/Release/net10.0/PortKiller.UpdateAdmin.Api.dll');
const dataRoot = join(tmpdir(), `PortKillBrowser-${process.pid}`);
const child = spawn(executable, [assembly, '--urls', 'http://127.0.0.1:15081', '--Administration:DataRoot', dataRoot], {
  cwd: repository, stdio: 'inherit', windowsHide: true, env: { ...process.env, PORTKILL_ADMIN_CONFIG: '' },
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.on('exit', (code) => process.exit(code ?? 1));
