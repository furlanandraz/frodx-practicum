#!/usr/bin/env bash
# Runs ON the server. deploy.yml pipes it over SSH when DEPLOY_MODE=ssh; in mock mode
# it is only printed.
#
# One-time server setup: a clone of this repository in APP_DIR, plus a .env there with the
# real order API and mail server. Only compose.yml is used, so the test services never run here.
#
# Required env: APP_DIR, DEPLOY_SHA (the merge commit that passed the tests)
set -euo pipefail

: "${APP_DIR:?APP_DIR is required}"
: "${DEPLOY_SHA:?DEPLOY_SHA is required}"

cd "$APP_DIR"
git fetch --quiet origin
git checkout --quiet --detach "$DEPLOY_SHA"

# Builds the worker image from that commit, then replaces the running container. A failed build
# fails the job and leaves the old worker running. The database and logs live in named volumes,
# so they survive. Whether the new worker works shows in its first cycle's log (and an email for
# 404, 401 or schema drift). Rollback = revert the merge on main, which deploys again.
docker compose up --detach --build
docker image prune --force

echo "Deployed $DEPLOY_SHA"
