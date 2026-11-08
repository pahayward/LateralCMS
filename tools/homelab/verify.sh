#!/usr/bin/env bash
# Checks a checkout on the machine you will run it on (written for the Debian home lab, runs anywhere with bash and curl):
# pull, build, run both test suites, then run the service the way it will really run (Production, its own data folder, a spare
# port) and walk the things only a real machine can show: file permissions, the first-run password file, the forced password
# change, the SSH reset command with the service running, an event end to end, and an asset uploaded, scanned and downloaded.
#
#   bash tools/homelab/verify.sh              pull, build, test, smoke-test
#   bash tools/homelab/verify.sh --no-pull    use the checkout as it is
#   bash tools/homelab/verify.sh --no-tests   skip the two test suites (the smoke test only)
#
# It makes a temporary folder, uses port 5192 (PORT=... to change), and removes both when it ends. It never prints a password.
set -u
cd "$(dirname "$0")/../.." || exit 2
PORT="${PORT:-5192}"
PULL=1
TESTS=1
for arg in "$@"; do
  case "$arg" in
    --no-pull) PULL=0 ;;
    --no-tests) TESTS=0 ;;
    *) echo "Unknown option $arg"; exit 2 ;;
  esac
done

pass=0
fail=0
ok() { echo "  ok    $1"; pass=$((pass + 1)); }
bad() { echo "  FAIL  $1"; fail=$((fail + 1)); }
same() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (wanted $3, got $2)"; fi; }
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
linux=0
[ "$(uname -s)" = "Linux" ] && linux=1

echo "== this machine"
uname -srm
command -v dotnet >/dev/null || { echo "dotnet is not installed (needs the .NET 9 SDK for the tests; the runtime is enough to only run it)"; exit 2; }
command -v curl >/dev/null || { echo "curl is not installed"; exit 2; }
dotnet --list-sdks
dotnet --list-runtimes | grep -i "aspnetcore" || true

if [ "$PULL" = 1 ]; then
  echo "== pull"
  git pull --ff-only || { echo "git pull failed"; exit 2; }
fi
git log --oneline -1

echo "== build (warnings are errors)"
if CI=true dotnet build Lateral.sln -c Release -nologo -v q > /tmp/lateral-build.log 2>&1; then ok "the solution builds with no warnings"; else bad "the build failed"; tail -20 /tmp/lateral-build.log; fi

if [ "$TESTS" = 1 ]; then
  echo "== tests"
  for project in tests/Lateral.Cms.Tests tests/Lateral.Cms.TestConsole.Tests; do
    out="$(dotnet test "$project" -c Release --no-build -nologo -v q 2>&1)"
    summary="$(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)"
    if echo "$summary" | grep -q 'Passed!'; then ok "$project: ${summary#*- }"; else bad "$project"; echo "$out" | grep -E "\[FAIL\]|Failed " | head -20; fi
  done
fi

echo "== run it for real"
WORK="$(mktemp -d)"
PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; sleep 1; rm -rf "$WORK"; }
trap cleanup EXIT

dotnet publish src/Lateral.Cms -c Release -o "$WORK/app" -nologo -v q > "$WORK/publish.log" 2>&1 || { bad "publish failed"; tail "$WORK/publish.log"; exit 1; }
mkdir -p "$WORK/data"
[ "$linux" = 1 ] && chmod 700 "$WORK/data"

SECRET="$(cd "$WORK/app" && dotnet Lateral.Cms.dll admin new-service-secret 2>/dev/null)"
if echo "$SECRET" | grep -Eq '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'; then ok "admin new-service-secret needs no configuration and makes a GUID"; else bad "admin new-service-secret"; fi

# .NET needs a path the operating system understands: under Git Bash on Windows that is not the /tmp form (it would quietly
# become C:\tmp), so convert it there. On Linux the two are the same.
DATA="$WORK/data"
command -v cygpath >/dev/null 2>&1 && DATA="$(cygpath -m "$WORK/data")"
SERVICE_ENV=(
  ASPNETCORE_ENVIRONMENT=Production "ASPNETCORE_URLS=http://127.0.0.1:$PORT" 'AllowedHosts=*'
  "Database__ConnectionString=Data Source=$DATA/cms.db" "Assets__Root=$DATA/assets"
  Auth__Cms__Username=cms-ingest-svc "Auth__Cms__Passwords__0=$SECRET"
)

# exec, so the process id is the service's own and cleanup really stops it.
(cd "$WORK/app" && exec env "${SERVICE_ENV[@]}" dotnet Lateral.Cms.dll > "$WORK/service.log" 2>&1) &
PID=$!
up=0
for _ in $(seq 1 60); do [ "$(code "http://127.0.0.1:$PORT/health")" = "200" ] && { up=1; break; }; sleep 0.5; done
same "the service starts in Production and answers /health" "$up" "1"
[ "$up" = 1 ] || { tail -30 "$WORK/service.log"; exit 1; }
B="http://127.0.0.1:$PORT"

FILE="$WORK/data/initial-admin-password.txt"
if [ -f "$FILE" ]; then ok "first run wrote the temporary admin password file"; else bad "no initial-admin-password.txt"; fi
if [ "$linux" = 1 ]; then same "that file is private to the service account (mode 600)" "$(stat -c %a "$FILE" 2>/dev/null)" "600"; fi
TEMP_PW="$(grep '^password: ' "$FILE" 2>/dev/null | cut -c11-)"
if grep -q "$TEMP_PW" "$WORK/service.log" 2>/dev/null; then bad "the temporary password is in the log"; else ok "the temporary password is not in the log"; fi
if [ "$linux" = 1 ] && [ -d "$WORK/data/keys" ]; then same "the session key folder is private (mode 700)" "$(stat -c %a "$WORK/data/keys")" "700"; fi

JAR="$WORK/jar"
login="$(curl -s -c "$JAR" -H 'Content-Type: application/json' -d "{\"username\":\"admin\",\"password\":\"$TEMP_PW\"}" "$B/api/auth/login")"
if echo "$login" | grep -q '"mustChangePassword":true'; then ok "signing in with the temporary password works and says it must be replaced"; else bad "sign-in: $login"; fi
grep -q HttpOnly "$JAR" && ok "the session cookie is HttpOnly" || bad "the session cookie is not HttpOnly"
same "until it is replaced, everything else is refused (403)" "$(code -b "$JAR" "$B/api/admin/users")" "403"

NEW_PW="Home-lab-$(date +%s)-ok"
same "the password can be changed" "$(code -b "$JAR" -c "$JAR" -H 'X-Requested-With: verify' -H 'Content-Type: application/json' -d "{\"current\":\"$TEMP_PW\",\"newPassword\":\"$NEW_PW\"}" "$B/api/auth/password")" "200"
same "the session carries on and is now allowed (200)" "$(code -b "$JAR" "$B/api/admin/users")" "200"
[ ! -f "$FILE" ] && ok "the temporary password file is gone" || bad "the temporary password file is still there"
same "the old password no longer works over Basic (401)" "$(code -u "admin:$TEMP_PW" "$B/api/auth/me")" "401"
same "the new one does (200)" "$(code -u "admin:$NEW_PW" "$B/api/auth/me")" "200"

echo "-- an event, end to end"
CMSAUTH="cms-ingest-svc:$SECRET"
NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
ack="$(curl -s -u "$CMSAUTH" -H 'Content-Type: application/json' -d "[{\"type\":\"publish\",\"id\":\"smoke-1\",\"version\":1,\"payload\":{\"title\":\"hello\"},\"timestamp\":\"$NOW\"}]" -w ' %{http_code}' "$B/cms/events")"
same "the webhook answers 202" "${ack##* }" "202"
BATCH="$(echo "$ack" | sed -n 's/.*"batchId":"\([^"]*\)".*/\1/p')"
state=""
for _ in $(seq 1 40); do state="$(curl -s -u "$CMSAUTH" "$B/cms/batches/$BATCH" | sed -n 's/.*"status":"\([^"]*\)".*/\1/p')"; [ "$state" = "completed" ] && break; sleep 0.25; done
same "the worker applies the batch" "$state" "completed"
same "an admin reads the record" "$(code -u "admin:$NEW_PW" "$B/api/entities/smoke-1")" "200"

echo "-- the API reference (Swagger)"
same "the OpenAPI document needs a sign-in (401)" "$(code "$B/swagger/v1/swagger.json")" "401"
same "the Swagger page needs a sign-in (401)" "$(code "$B/swagger/index.html")" "401"
same "the OpenAPI document opens for an admin (200)" "$(code -u "admin:$NEW_PW" "$B/swagger/v1/swagger.json")" "200"
if curl -s -u "admin:$NEW_PW" "$B/swagger/v1/swagger.json" | grep -q '"/cms/events"'; then ok "the document describes the webhook"; else bad "the document does not mention /cms/events"; fi
csp="$(curl -s -D - -o /dev/null -u "admin:$NEW_PW" "$B/swagger/index.html" | grep -i '^content-security-policy')"
if echo "$csp" | grep -q "script-src 'self'" && ! echo "$csp" | grep -q "unsafe-eval"; then ok "the page runs only its own scripts, no generated code"; else bad "the page's content policy is wrong: $csp"; fi

echo "-- an asset: upload, scan, ranged download"
head -c 262144 /dev/urandom > "$WORK/picture.bin"
printf '\x89PNG\r\n\x1a\n' | cat - "$WORK/picture.bin" > "$WORK/picture.png"
up="$(curl -s -u "$CMSAUTH" -X PUT -H 'Content-Type: image/png' --data-binary "@$WORK/picture.png" -w ' %{http_code}' "$B/cms/assets/smoke.png")"
same "the upload is accepted (202)" "${up##* }" "202"
ASSET="$(echo "$up" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
astate=""
for _ in $(seq 1 60); do astate="$(curl -s -u "$CMSAUTH" "$B/cms/assets/$ASSET" | sed -n 's/.*"state":"\([^"]*\)".*/\1/p')"; [ "$astate" = "clean" ] && break; sleep 0.5; done
same "the scanner clears it" "$astate" "clean"
same "a ranged download gives 206" "$(code -u "admin:$NEW_PW" -H 'Range: bytes=0-99' "$B/api/assets/$ASSET")" "206"
curl -s -u "admin:$NEW_PW" "$B/api/assets/$ASSET" -o "$WORK/back.png"
same "the downloaded bytes are the uploaded bytes" "$(sha256sum < "$WORK/back.png" | cut -d' ' -f1)" "$(sha256sum < "$WORK/picture.png" | cut -d' ' -f1)"

echo "-- locked out: reset the admin password over SSH while the service is running"
RESET_PW="Reset-by-ssh-$(date +%s)"
JAR2="$WORK/jar2"
curl -s -o /dev/null -c "$JAR2" -H 'Content-Type: application/json' -d "{\"username\":\"admin\",\"password\":\"$NEW_PW\"}" "$B/api/auth/login"
same "an open session works before the reset" "$(code -b "$JAR2" "$B/api/admin/users")" "200"
echo "$RESET_PW" | (cd "$WORK/app" && env "${SERVICE_ENV[@]}" dotnet Lateral.Cms.dll admin set-password admin --stdin) > "$WORK/cli.log" 2>&1
if grep -q "was changed" "$WORK/cli.log"; then ok "the command-line tool changed the password"; else bad "set-password: $(cat "$WORK/cli.log")"; fi
if grep -q "$RESET_PW" "$WORK/cli.log"; then bad "the tool printed the password"; else ok "the tool did not print the password"; fi
sleep 7
same "the open session ended" "$(code -b "$JAR2" "$B/api/admin/users")" "401"
same "the old password is refused" "$(code -u "admin:$NEW_PW" "$B/api/auth/me")" "401"
same "the new one works" "$(code -u "admin:$RESET_PW" "$B/api/auth/me")" "200"

echo "-- the log"
if grep -q -e "$NEW_PW" -e "$RESET_PW" -e "$SECRET" "$WORK/service.log"; then bad "a password or secret is in the service log"; else ok "no password or secret in the service log"; fi
errors="$(grep -c '"LogLevel":"Error"' "$WORK/service.log")"
same "no error lines in the service log" "$errors" "0"

if command -v systemd-run >/dev/null && [ "$linux" = 1 ]; then
  echo "-- systemd-run is available (docs/OPERATIONS.md uses it for the admin command); try it yourself against your real install"
fi

echo
echo "== $pass passed, $fail failed"
[ "$fail" = 0 ]
