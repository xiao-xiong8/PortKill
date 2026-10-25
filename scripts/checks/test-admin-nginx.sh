#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
admin="$repository/deploy/nginx/portkiller-admin.conf.example"
download="$repository/deploy/nginx/portkiller-download-tls.conf.example"
legacy="$repository/deploy/nginx/portkiller-updates.conf"
for required in "$admin" "$download" "$legacy"; do test -f "$required"; done

grep -Fq 'location /api/v1/' "$admin"
grep -Fq 'location = /api/v1' "$admin"
grep -Fq 'events/stream' "$admin"
grep -Fq 'proxy_buffering off' "$admin"
grep -Fq 'proxy_request_buffering off' "$admin"
grep -Fq 'client_max_body_size 257m' "$admin"
grep -Fq 'X-Forwarded-Proto https' "$admin"
grep -Fq 'try_files $uri $uri/ /index.html' "$admin"
grep -Fq 'location = /stable/manifest.json' "$download"
grep -Fq 'immutable' "$download"
grep -Fq 'max_ranges 1' "$download"
grep -Fq 'location = /stable/events' "$download"
grep -Fq 'location / { return 404; }' "$download"
grep -Fq 'location / { return 404; }' "$legacy"

# Templates must not contain a private key or a real deployment hostname.
! grep -Eiq 'BEGIN (RSA |EC )?PRIVATE KEY|password[[:space:]]*=' "$admin" "$download" "$legacy"
grep -Fq 'admin.example.com' "$admin"
grep -Fq 'updates.example.com' "$download"

echo "PASS admin Nginx templates"
