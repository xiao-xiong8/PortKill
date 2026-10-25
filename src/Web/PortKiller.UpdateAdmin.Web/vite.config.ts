import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const certificateDirectory = process.env.PORTKILL_ADMIN_TEST_CERT_DIRECTORY;

export default defineConfig({
  plugins: [vue()],
  server: { host: '127.0.0.1', port: 5173, strictPort: true,
    https: certificateDirectory ? { cert: readFileSync(join(certificateDirectory, 'certificate.pem')), key: readFileSync(join(certificateDirectory, 'key.pem')) } : undefined,
    proxy: { '/api': { target: process.env.PORTKILL_ADMIN_DEV_PROXY ?? 'http://127.0.0.1:5081', secure: !certificateDirectory } } },
  preview: { host: '127.0.0.1', port: 4173, strictPort: true },
  build: { sourcemap: false },
});
