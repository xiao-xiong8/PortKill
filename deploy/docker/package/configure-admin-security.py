"""Generate a reviewed Caddy candidate; never edits the live input in place."""
import argparse
from pathlib import Path
import re

BEGIN = '    # BEGIN PortKill admin security v1'
END = '    # END PortKill admin security v1'
PROTOCOLS = '    # PortKill protocols: h1/h2\n    servers {\n        protocols h1 h2\n    }'
CSP = ("default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
       "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; "
       "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'")


def configure(text, enforce=False, hsts=86400):
    if hsts not in (86400, 31536000):
        raise ValueError('Choose the reviewed one-day or one-year HSTS policy')
    text = text.replace('\r\n', '\n')
    anchor = '{$ADMIN_DOMAIN} {'
    if text.count(anchor) != 1 or text.count('{$UPDATES_DOMAIN} {') != 1:
        raise ValueError('Expected one management and one downloads site; review custom host layout')
    # Keep the complete downloads site and its public routes intact.
    admin, downloads = text.split('{$UPDATES_DOMAIN} {', 1)
    if BEGIN in admin:
        if admin.count(BEGIN) != 1 or admin.count(END) != 1:
            raise ValueError('Ambiguous managed security section')
        admin = re.sub(re.escape(BEGIN) + r'.*?' + re.escape(END) + r'\n', '', admin, flags=re.S)
    else:
        health = '    handle /health/* {\n        reverse_proxy api:5081\n    }\n'
        if admin.count(health) != 1:
            raise ValueError('Custom health routing requires review')
        admin = admin.replace(health, '')
        if re.search(r'(?m)^\s*(header|servers)\b', admin):
            raise ValueError('Existing headers/protocol settings require review before merging')
    if '    # PortKill protocols: h1/h2\n' in admin and admin.count(PROTOCOLS) != 1:
        raise ValueError('Managed protocol settings changed; review configuration drift')
    if PROTOCOLS not in admin:
        if not admin.lstrip().startswith('{\n'):
            raise ValueError('Expected the original global options block')
        at = admin.index('{') + 1
        admin = admin[:at] + '\n' + PROTOCOLS + admin[at:]
    policy = 'Content-Security-Policy' if enforce else 'Content-Security-Policy-Report-Only'
    block = f'''{BEGIN}
    header {{
        defer
        -Server
        -Via
        -Alt-Svc
        -Content-Security-Policy
        -Content-Security-Policy-Report-Only
        X-Frame-Options DENY
        X-Content-Type-Options nosniff
        Referrer-Policy no-referrer
        Permissions-Policy "camera=(), microphone=(), geolocation=()"
        Cross-Origin-Opener-Policy same-origin
        Cross-Origin-Resource-Policy same-origin
        Strict-Transport-Security "max-age={hsts}"
        {policy} "{CSP}"
    }}
    @private_health path /health /health/*
    handle @private_health {{
        respond 404
    }}
{END}
'''
    # Deletions and additions of the same CSP key in a single header handler conflict.
    other = 'Content-Security-Policy-Report-Only' if enforce else 'Content-Security-Policy'
    block = block.replace('        -Content-Security-Policy\n        -Content-Security-Policy-Report-Only\n', f'        -{other}\n')
    # Caddy proxy failures enter a separate error route; site headers do not wrap that route.
    headers = block[block.index('    header {'):block.index('    @private_health')]
    errors = ('    handle_errors {\n' + ''.join('    ' + line + '\n' for line in headers.splitlines())
              + '        respond "{err.status_code} {err.status_text}" {err.status_code}\n    }\n')
    block = block.replace('    @private_health', errors + '    @private_health', 1)
    admin = admin.replace(anchor + '\n', anchor + '\n' + block, 1)
    # Reverse proxy may insert its own Server/Via after upstream processing; site headers are deferred.
    return admin + '{$UPDATES_DOMAIN} {' + downloads


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--enforce', action='store_true')
    parser.add_argument('--hsts', type=int, choices=(86400, 31536000), default=86400)
    args = parser.parse_args()
    if args.input.resolve() == args.output.resolve():
        parser.error('Use a separate candidate file')
    result = configure(args.input.read_text(encoding='utf-8-sig'), args.enforce, args.hsts)
    args.output.write_text(result, encoding='utf-8', newline='\n')


if __name__ == '__main__':
    main()
