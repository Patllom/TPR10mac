#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
node ops/migrations/validate-local.mjs
exec dotnet ef database update --project backend/src/TPR10.Api
