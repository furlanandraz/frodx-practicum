#!/usr/bin/env bash
# End-to-end checks against a running compose stack. Used by CI; works locally in Git Bash too:
#   cp .env.example .env && docker compose up --detach --build --wait && bash scripts/smoke-test.sh
#
# The worker exposes nothing, so the checks read its log and the Mailpit inbox. The mock's
# behaviour is changed through its control endpoint and reset at the end.
set -euo pipefail

MOCK_URL="${MOCK_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
RUN_TIMEOUT_SECONDS="${RUN_TIMEOUT_SECONDS:-60}"

pass() { echo "ok    $*"; }
fail() { echo "FAIL  $*" >&2; exit 1; }

worker_log() { docker compose logs worker 2>/dev/null; }

# Number of finished cycles (completed or failed) in the worker's log so far.
finished_runs() { worker_log | grep -cE "Ingestion run [0-9a-f-]+ (completed|failed)" || true; }

# Number of cycles that started, finished or not.
started_runs() { worker_log | grep -cE "Ingestion run [0-9a-f-]+ started" || true; }

# Waits until the worker has finished more cycles than $1, then prints the newest cycle's result line.
wait_for_run_after() {
  local before=$1
  for _ in $(seq 1 "$RUN_TIMEOUT_SECONDS"); do
    if [ "$(finished_runs)" -gt "$before" ]; then
      worker_log | grep -E "Ingestion run [0-9a-f-]+ (completed|failed)" | tail -1
      return 0
    fi
    sleep 1
  done
  fail "worker did not finish a cycle within ${RUN_TIMEOUT_SECONDS}s"
}

# Restarts the worker, which runs a cycle immediately on startup, and returns that cycle's result line.
restart_worker_and_wait() {
  local before
  before=$(finished_runs)
  docker compose restart worker > /dev/null 2>&1
  wait_for_run_after "$before"
}

# Changes what the mock does, e.g. mock '{"scenario":"orders-drift"}'. Fields left out stay as they are.
mock() {
  curl --fail --silent --show-error --request PUT "$MOCK_URL/_mock/settings" \
    --header "Content-Type: application/json" --data "$1" > /dev/null
}
reset_mock() { curl --fail --silent --show-error --request DELETE "$MOCK_URL/_mock/settings" > /dev/null; }

clear_inbox() { curl --fail --silent --show-error --request DELETE "$MAILPIT_URL/api/v1/messages" > /dev/null; }
inbox_has() { sleep 2; curl --fail --silent --show-error "$MAILPIT_URL/api/v1/messages" | grep -qF "$1"; }
inbox_empty() { sleep 2; curl --fail --silent --show-error "$MAILPIT_URL/api/v1/messages" | grep -q '"total":0'; }

# --- 0. Same starting point every time: default mock, worker with an empty database ---
reset_mock
docker compose rm --stop --force worker > /dev/null 2>&1
docker volume rm order-ingestion_worker-data > /dev/null 2>&1 || true
docker compose up --detach --wait worker > /dev/null 2>&1

# --- 1. The mock API -------------------------------------------------------
curl --fail --silent --show-error "$MOCK_URL/orders" > /dev/null || fail "mock API $MOCK_URL/orders is not reachable"
status=$(curl --silent --output /dev/null --write-out '%{http_code}' "$MOCK_URL/orders?scenario=..%2Fsecrets")
[ "$status" = "400" ] || fail "mock API accepted an unsafe scenario name (HTTP $status)"
pass "mock API answers and rejects unsafe scenario names"

# --- 2. Startup cycle ------------------------------------------------------
summary=$(wait_for_run_after 0)
grep -q "completed" <<<"$summary" || fail "startup cycle did not complete: $summary"
pass "startup cycle completed"

# --- 3. Persistence + dedupe: a restart must not store anything twice ------
summary=$(restart_worker_and_wait)
grep -q "inserted 0," <<<"$summary" || fail "second cycle stored orders again: $summary"
pass "second cycle stored nothing new (cursor + dedupe survive a restart)"

# --- 4. Crash mid-cycle: nothing is lost, the next start picks up ----------
# orders-new has one order we don't have yet (ORD-1006). The mock answers slowly, and the
# worker is killed with SIGKILL (like a power cut) while it waits.
mock '{"scenario":"orders-new","latencyMs":5000}'
finished_before=$(finished_runs)
started_before=$(started_runs)
docker compose restart worker > /dev/null 2>&1
for _ in $(seq 1 "$RUN_TIMEOUT_SECONDS"); do [ "$(started_runs)" -gt "$started_before" ] && break; sleep 1; done
sleep 1
docker compose kill worker > /dev/null 2>&1
[ "$(finished_runs)" -eq "$finished_before" ] || fail "the cycle finished before the worker was killed"

mock '{"latencyMs":0}'
docker compose start worker > /dev/null 2>&1
summary=$(wait_for_run_after "$finished_before")
grep -q "inserted 1," <<<"$summary" || fail "cycle after the crash did not pick up the missed order: $summary"
worker_log | grep -q "was interrupted before it finished" || fail "interrupted cycle was not reported"
pass "worker killed mid-cycle; the next start detected it and stored the missed order"

# Emails go out for exactly three failures: 404, 401 and schema drift.

# --- 5. Schema drift (fields added): early return, emailed -----------------
clear_inbox
mock '{"scenario":"orders-drift"}'
summary=$(restart_worker_and_wait)
grep -q "failed (schema drift, nothing stored)" <<<"$summary" || fail "added fields were not schema drift: $summary"
inbox_has "no longer matches our schema" || fail "no schema drift email"
pass "added fields: schema drift, nothing stored, emailed"

# --- 6. Schema drift (records broken): early return, emailed ---------------
clear_inbox
mock '{"scenario":"orders-broken"}'
summary=$(restart_worker_and_wait)
grep -q "failed (schema drift, nothing stored).*ORD-1008" <<<"$summary" || fail "broken records were not schema drift: $summary"
inbox_has "no longer matches our schema" || fail "no schema drift email"
pass "broken records: schema drift naming the first bad order, nothing stored, emailed"

# --- 7. 503: one attempt, logged, no email; the next cycle tries again ------
clear_inbox
mock '{"scenario":"orders","failureRate":1,"failureStatusCode":503}'
summary=$(restart_worker_and_wait)
grep -q "failed (the next cycle tries again).*503" <<<"$summary" || fail "503 was not logged as left for the next cycle: $summary"
inbox_empty || fail "a 503 should not email"
pass "503: logged, no email"

# --- 8. 404: early return, emailed -----------------------------------------
clear_inbox
mock '{"failureStatusCode":404}'
summary=$(restart_worker_and_wait)
grep -q "failed (404)" <<<"$summary" || fail "404 was not reported: $summary"
inbox_has "returned 404" || fail "no 404 email"
pass "404: logged and emailed"

# --- 9. 401: early return, emailed -----------------------------------------
clear_inbox
mock '{"failureStatusCode":401}'
summary=$(restart_worker_and_wait)
grep -q "failed (401)" <<<"$summary" || fail "401 was not reported: $summary"
inbox_has "(401)" || fail "no 401 email"
pass "401: logged and emailed"

# --- Reset -----------------------------------------------------------------
reset_mock
docker compose restart worker > /dev/null 2>&1
pass "mock API reset to its configured settings"
