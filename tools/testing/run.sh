#!/usr/bin/env bash
# Runs both suites with .trx output and rebuilds the engineering console's Testing page data from them.
#   bash tools/testing/run.sh
set -u
cd "$(dirname "$0")/../.." || exit 2
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
status=0
dotnet test tests/Lateral.Cms.TestConsole.Tests --logger "trx;LogFileName=console.trx" --results-directory "$out" || status=1
dotnet test tests/Lateral.Cms.Tests --logger "trx;LogFileName=service.trx" --results-directory "$out" || status=1
node tools/testing/build.mjs "$out"
exit $status
