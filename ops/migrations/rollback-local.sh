#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo 'ต้องระบุ migration เป้าหมายสำหรับฐานข้อมูล local' >&2
  exit 1
fi
case "$1" in *[!a-zA-Z0-9_]*) echo 'ชื่อ migration ไม่ถูกต้อง' >&2; exit 1 ;; esac
node ops/migrations/validate-local.mjs
exec dotnet ef database update "$1" --project backend/src/TPR10.Api
