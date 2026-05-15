#!/usr/bin/env bash
# Sync the repo to C:\winpepper on the Windows VM, excluding build outputs.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost \
    'powershell -NoProfile -Command "if (!(Test-Path C:\winpepper)) { New-Item -ItemType Directory -Path C:\winpepper | Out-Null }"' >/dev/null

tar --exclude='./.git' \
    --exclude='./bin' \
    --exclude='./obj' \
    --exclude='./TestResults' \
    --exclude='*/bin' \
    --exclude='*/obj' \
    --exclude='./artifacts' \
    -cf - -C "$HERE" . \
  | sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost \
      'tar -xf - -C C:/winpepper'

echo "Synced $HERE to localhost:2222 C:\\winpepper"
