#!/usr/bin/env bash
# Drives a running Lookout (AppHost: Backend, frontend, Runner, browserless, gluetun) for agents.
# Run from the repo root. See SKILL.md next to this file.
set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
SKILL_DIR="$ROOT/.claude/skills/run-lookout"
STATE="$ROOT/.docker-volumes/run-lookout" # gitignored, and under /Users, which Docker Desktop shares
JAR="$STATE/cookies"
BACKEND=https://localhost:7070
FRONTEND=http://localhost:4200
BEGIN='# >>> run-lookout (temporary, removed once the AppHost has started)'
END='# <<< run-lookout'

die() { echo "lookout.sh: $*" >&2; exit 1; }

# Removes the overlay block from .env, if any
strip_overlay() {
  perl -0pi -e "s/\n?\Q$BEGIN\E.*?\Q$END\E\n?/\n/s" "$ROOT/.env"
}

env_value() {
  python3 - "$ROOT/.env" "$1" <<'EOF'
import sys
for line in open(sys.argv[1]):
    line = line.strip()
    if line.startswith('#') or '=' not in line:
        continue
    k, v = line.split('=', 1)
    if k.strip() == sys.argv[2]:
        print(v.strip().strip('"').strip("'"))
EOF
}

api() {
  local method=$1 path=$2; shift 2
  # The CSRF check wants X-Lookout-Request and a same-origin Origin on anything that changes data
  curl -sk -b "$JAR" -c "$JAR" -X "$method" "$BACKEND$path" \
    -H 'X-Lookout-Request: 1' -H "Origin: $BACKEND" "$@"
}

cmd_start() {
  local limit=3 keep=0
  while [ $# -gt 0 ]; do
    case $1 in
      --limit) limit=$2; shift 2 ;;
      --keep-data) keep=1; shift ;;
      *) die "start: unknown option $1" ;;
    esac
  done
  [ -f "$ROOT/.env" ] || die "no .env at the repo root; it needs LOOKOUT_OWNER_USERNAME and LOOKOUT_OWNER_PASSWORD"
  [ -n "$(env_value LOOKOUT_OWNER_PASSWORD)" ] || die ".env has no LOOKOUT_OWNER_PASSWORD"
  docker info >/dev/null 2>&1 || die "Docker isn't running"

  mkdir -p "$STATE/data/db" "$STATE/data/scripts"
  # Emptied, not deleted and recreated: Docker Desktop's file sharing can go on seeing a recreated directory as missing
  # for a while, and the Runner's bind mount of it then fails for good
  [ "$keep" = 1 ] || find "$STATE/data/db" "$STATE/data/scripts" -mindepth 1 -delete

  # The AppHost reads its settings only from .env, once, at start. Throwaway data directories keep the Owner's
  # development database and scripts out of it.
  strip_overlay
  {
    echo "$BEGIN"
    echo "LOOKOUT_MAX_CONCURRENT_RUNS=$limit"
    echo "LOOKOUT_DATABASE_DIR=$STATE/data/db"
    echo "LOOKOUT_WATCH_SCRIPTS_DIR=$STATE/data/scripts"
    echo "$END"
  } >> "$ROOT/.env"
  trap strip_overlay EXIT

  echo "Starting the AppHost (limit $limit); the first start builds the Runner's image and takes minutes..."
  (cd "$ROOT" && aspire run --detach --non-interactive --nologo --apphost src/AppHost) > "$STATE/apphost.log" 2>&1 \
    || { cat "$STATE/apphost.log"; die "aspire run failed"; }
  strip_overlay
  trap - EXIT

  # Detached returns once the AppHost is up, not its resources: wait for the Backend, then the frontend. A resource
  # that fails to start isn't retried, and the Backend would wait for it for good, so stop at the first one
  local failed
  for i in $(seq 1 180); do
    [ "$(curl -sk -o /dev/null -w '%{http_code}' $BACKEND/api/auth/me)" = 401 ] && break
    if [ $((i % 5)) = 0 ]; then
      failed=$(failed_resources)
      [ -z "$failed" ] || die "failed to start: $failed. See: aspire logs <name> --apphost src/AppHost; then $0 stop"
    fi
    sleep 2
  done
  [ "$(curl -sk -o /dev/null -w '%{http_code}' $BACKEND/api/auth/me)" = 401 ] || die "the Backend didn't come up; see $STATE/apphost.log and the Aspire dashboard"
  for _ in $(seq 1 90); do
    [ "$(curl -s -o /dev/null -w '%{http_code}' $FRONTEND/)" = 200 ] && break
    sleep 2
  done
  echo "Backend: $BACKEND  Frontend: $FRONTEND"
  cmd_login
}

# The AppHost's resources that failed to start, by name
failed_resources() {
  (cd "$ROOT" && aspire describe --format json --nologo --apphost src/AppHost 2>/dev/null) | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except ValueError:
    sys.exit()
resources = d.get("resources", []) if isinstance(d, dict) else d
# frontend-browser is the VS Code browser debugger, which always fails to start outside VS Code
print(" ".join(r.get("name", "?") for r in resources
               if r.get("state") == "FailedToStart" and not r.get("name", "").startswith("frontend-browser")))'
}

cmd_stop() {
  (cd "$ROOT" && aspire stop --non-interactive --nologo --apphost src/AppHost) || true
  strip_overlay
  rm -f "$JAR"
  # aspire stop returns before the containers are gone; gluetun takes a few seconds longer
  for _ in $(seq 1 30); do
    docker ps --format '{{.Names}}' | grep -qE '^(gluetun|gluetun-updater|browserless|runner|tunnel-proxy)-' || return 0
    sleep 1
  done
  echo "lookout.sh: some of the AppHost's containers are still running:" >&2
  docker ps --format '{{.Names}}' | grep -E '^(gluetun|gluetun-updater|browserless|runner|tunnel-proxy)-' >&2
}

cmd_login() {
  mkdir -p "$STATE"
  python3 -c 'import json,sys; print(json.dumps({"username": sys.argv[1], "password": sys.argv[2]}))' \
    "$(env_value LOOKOUT_OWNER_USERNAME)" "$(env_value LOOKOUT_OWNER_PASSWORD)" \
    | api POST /api/auth/login -H 'Content-Type: application/json' --data @- -o /dev/null -w 'login: %{http_code}\n'
}

# watch NAME true|false SCRIPT.ts -> prints the new watch's id
cmd_watch() {
  [ $# -eq 3 ] || die "usage: watch NAME true|false SCRIPT"
  api POST /api/Watches -F "Name=$1" -F "UseVpn=$2" -F "script=@$3" \
    | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["id"]) if "id" in d else sys.exit(json.dumps(d))'
}

# run ID... -> queues a run of each watch
cmd_run() {
  for w in "$@"; do api POST "/api/Watches/$w/runs" -o /dev/null -w "watch $w: %{http_code}\n"; done
}

# runs ID... -> each watch's latest runs, oldest first
cmd_runs() {
  local w json
  for w in "$@"; do
    json=$(api GET "/api/Watches/$w/runs?limit=10")
    python3 - "$w" "$json" <<'EOF'
import json, sys
w, body = sys.argv[1], sys.argv[2]
status = {0: "Created", 1: "Running", 2: "Done"}
for r in reversed(json.loads(body)):
    t = lambda k: (r.get(k) or "")[11:19]
    what = r.get("content") or r.get("failureReason") or r.get("error") or ""
    print(f"watch {w} run {r['id']:>3} {status.get(r['status'], r['status']):8} start={t('startTime'):8} "
          f"end={t('endTime'):8} region={r.get('region')} retryOf={r.get('retryOfRunId')} {what[:80]}")
EOF
  done
}

# wait-idle ID... -> waits until none of the watches has a run waiting or running, and no VPN switch is in progress
cmd_wait_idle() {
  # Idle twice in a row, 2 seconds apart: the Backend picks up queued switches and runs only once a second, so one look
  # can fall in the gap between one switch or run ending and the next starting
  local listed idle=0
  for _ in $(seq 1 300); do
    listed=$(cmd_runs "$@") || die "couldn't list runs"
    if ! grep -q 'Created\|Running' <<<"$listed" \
      && [ "$(api GET /api/Vpn | python3 -c 'import json,sys; print(json.load(sys.stdin).get("switch"))')" = None ]; then
      idle=$((idle + 1))
      [ "$idle" -ge 2 ] && return 0
    else
      idle=0
    fi
    sleep 2
  done
  die "still busy after 10 minutes"
}

cmd_vpn() {
  api GET /api/Vpn | python3 -c '
import json, sys
d = json.load(sys.stdin)
s = d.get("settings") or {}
print("configured:", d.get("configured"), " region:", s.get("currentRegion"), " switch:", d.get("switch"),
      " publicIp:", (d.get("publicIp") or {}).get("publicIp"), " lastError:", s.get("lastError"))'
}

# The VPN's switch in progress and last switch time, to tell when a switch has begun or is done
vpn_switch_state() {
  api GET /api/Vpn | python3 -c '
import json, sys
d = json.load(sys.stdin)
print(d.get("switch"), (d.get("settings") or {}).get("lastSwitchTime"))'
}

# switch [REGION] -> asks for a region switch, to a random region without one, and returns once it's in progress or
# done: the Backend only picks up switch requests once a second, so wait-idle right after would see nothing yet
cmd_switch() {
  local before code
  before=$(vpn_switch_state)
  code=$(python3 -c 'import json,sys; print(json.dumps({"region": sys.argv[1] if len(sys.argv) > 1 else None}))' "$@" \
    | api POST /api/Vpn/switch -H 'Content-Type: application/json' --data @- -o /dev/stderr -w '%{http_code}')
  echo "switch: $code"
  [ "$code" = 202 ] || return 1
  for _ in $(seq 1 20); do
    [ "$(vpn_switch_state)" != "$before" ] && return 0
    sleep 0.5
  done
  echo "lookout.sh: the switch hadn't begun after 10 seconds" >&2
}

# screenshot OUT.png [PATH] -> signs in through the frontend and screenshots PATH (default /)
cmd_screenshot() {
  [ $# -ge 1 ] || die "usage: screenshot OUT.png [PATH]"
  (cd "$ROOT" && LOOKOUT_USER="$(env_value LOOKOUT_OWNER_USERNAME)" LOOKOUT_PASSWORD="$(env_value LOOKOUT_OWNER_PASSWORD)" \
    node "$SKILL_DIR/screenshot.mjs" "$FRONTEND" "${2:-/}" "$1")
}

sub=${1:-}; [ -n "$sub" ] || die "usage: lookout.sh start|stop|login|api|watch|run|runs|wait-idle|vpn|switch|screenshot ..."
shift
case $sub in
  start) cmd_start "$@" ;;
  stop) cmd_stop ;;
  login) cmd_login ;;
  api) api "$@" ;;
  watch) cmd_watch "$@" ;;
  run) cmd_run "$@" ;;
  runs) cmd_runs "$@" ;;
  wait-idle) cmd_wait_idle "$@" ;;
  vpn) cmd_vpn ;;
  switch) cmd_switch "$@" ;;
  screenshot) cmd_screenshot "$@" ;;
  *) die "unknown command $sub" ;;
esac
