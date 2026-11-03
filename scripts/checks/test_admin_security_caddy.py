"""Exercise generated headers through a real Caddy binary on isolated loopback TLS ports."""
import argparse
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import runpy
import socket
import ssl
import subprocess
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[2]


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Upstream(BaseHTTPRequestHandler):
    def do_GET(self):
        status = int(self.path.rsplit('/', 1)[1]) if '/status/' in self.path else (401 if self.path.endswith('/error') else 200)
        self.send_response(status)
        self.send_header('Server', 'Kestrel')
        self.send_header('Via', '1.1 Caddy')
        self.send_header('X-Frame-Options', 'SAMEORIGIN')
        self.send_header('Content-Type', 'text/event-stream' if self.path.endswith('/stream') else 'application/json')
        self.end_headers()
        self.wfile.write(b'data: {}\n\n' if self.path.endswith('/stream') else b'{}')

    def log_message(self, *args):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--caddy', type=Path, required=True)
    args = parser.parse_args()
    caddy = str(args.caddy.resolve(strict=True))
    configure = runpy.run_path(str(ROOT / 'deploy/docker/package/configure-admin-security.py'))['configure']
    source = runpy.run_path(str(Path(__file__).with_name('test_admin_security.py')))['SOURCE']
    with tempfile.TemporaryDirectory(prefix='portkill-caddy-') as temporary:
        folder = Path(temporary)
        (folder / 'index.html').write_text('<html>admin</html>', encoding='utf-8')
        (folder / 'asset.css').write_text('body{}', encoding='utf-8')
        (folder / 'sample.bin').write_bytes(b'public-download')
        upstream = ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
        threading.Thread(target=upstream.serve_forever, daemon=True).start()
        admin_port, download_port = free_port(), free_port()
        source = source.replace('email {$ACME_EMAIL}', 'admin off\n    auto_https disable_redirects\n    skip_install_trust\n    storage file_system {\n        root "' + folder.as_posix() + '/storage"\n    }')
        source = configure(source)
        source = source.replace('api:5081', '127.0.0.1:' + str(upstream.server_port))
        source = source.replace('/srv/web', folder.as_posix())
        source = source.replace('    header Access-Control-Allow-Origin *', '    root * "' + folder.as_posix() + '"\n    header Access-Control-Allow-Origin *')
        source = source.replace('    handle /stable/* {', '    handle /stable/* {\n        uri strip_prefix /stable')
        text = source
        text = text.replace('    handle /api/v1/* {', f'    handle /api/v1/proxy-error {{\n        reverse_proxy 127.0.0.1:{free_port()}\n    }}\n    handle /api/v1/* {{')
        text = text.replace('{$ADMIN_DOMAIN} {', f'https://127.0.0.1:{admin_port} {{\n    tls internal')
        text = text.replace('{$UPDATES_DOMAIN} {', f'https://127.0.0.1:{download_port} {{\n    tls internal')
        config = folder / 'Caddyfile'; config.write_text(text, encoding='utf-8')
        subprocess.run([caddy, 'validate', '--config', str(config), '--adapter', 'caddyfile'], check=True, capture_output=True)
        adapted = subprocess.run([caddy, 'adapt', '--config', str(config), '--adapter', 'caddyfile'], check=True, capture_output=True, text=True)
        servers = json.loads(adapted.stdout)['apps']['http']['servers'].values()
        assert all(server['protocols'] == ['h1', 'h2'] for server in servers)
        with (folder / 'caddy.log').open('w', encoding='utf-8') as log:
            process = subprocess.Popen([caddy, 'run', '--config', str(config), '--adapter', 'caddyfile'], stdout=log, stderr=log)
            try:
                def request(port, path):
                    connection = http.client.HTTPSConnection('127.0.0.1', port, context=ssl._create_unverified_context(), timeout=3)
                    try:
                        connection.request('GET', path)
                        response = connection.getresponse()
                        return response.status, response.getheaders(), response.read()
                    finally:
                        connection.close()

                deadline = time.monotonic() + 20
                while True:
                    try:
                        request(admin_port, '/'); break
                    except (OSError, ssl.SSLError):
                        if process.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError((folder / 'caddy.log').read_text(encoding='utf-8'))
                        time.sleep(.1)
                for path, expected in [('/', 200), ('/login?returnTo=/overview', 200), ('/asset.css', 200),
                                       ('/api/v1/auth/csrf', 200), ('/api/v1/error', 401), ('/api/v1/stream', 200),
                                       *[(f'/api/v1/status/{status}', status) for status in (400, 403, 404, 429, 500)],
                                       ('/api/v1/proxy-error', 502),
                                       ('/health', 404), ('/health/ready', 404)]:
                    status, pairs, body = request(admin_port, path)
                    headers = {key.lower(): value for key, value in pairs}
                    assert status == expected, (path, status)
                    assert headers['x-frame-options'] == 'DENY', (path, headers)
                    assert sum(key.lower() == 'x-frame-options' for key, _ in pairs) == 1
                    for key, value in [('x-content-type-options', 'nosniff'), ('referrer-policy', 'no-referrer'),
                                       ('cross-origin-opener-policy', 'same-origin'), ('cross-origin-resource-policy', 'same-origin'),
                                       ('strict-transport-security', 'max-age=86400')]:
                        assert headers[key] == value, (path, key, headers)
                    assert 'content-security-policy-report-only' in headers
                    assert all(key not in headers for key in ('server', 'via', 'alt-svc', 'cross-origin-embedder-policy'))
                    print('PASS management', path, status)
                status, pairs, body = request(download_port, '/stable/sample.bin')
                assert status == 200 and body == b'public-download'
                headers = {key.lower(): value for key, value in pairs}
                assert 'cross-origin-resource-policy' not in headers
                assert 'content-security-policy-report-only' not in headers
                print('PASS public download and h1/h2 configuration')
            finally:
                process.terminate(); process.wait(timeout=10)
                upstream.shutdown(); upstream.server_close()


if __name__ == '__main__':
    main()
