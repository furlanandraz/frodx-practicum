#!/usr/bin/env bash
# Runs ON the server. deploy.yml pipes it over SSH when DEPLOY_MODE=ssh; in mock mode
# it is only printed. Expects compose.yml, compose.deploy.yml and .env in APP_DIR.
#
# Required env: APP_DIR, IMAGE_REGISTRY (e.g. ghcr.io/<owner>), IMAGE_TAG (git sha)
set -euo pipefail

: "${APP_DIR:?APP_DIR is required}"
: "${IMAGE_REGISTRY:?IMAGE_REGISTRY is required}"
: "${IMAGE_TAG:?IMAGE_TAG is required}"
export IMAGE_REGISTRY IMAGE_TAG

cd "$APP_DIR"
compose=(docker compose -f compose.yml -f compose.deploy.yml)

"${compose[@]}" pull
# --wait blocks until every container is running (and healthy, where it has a health check),
# so a release that can't start fails the deploy job.
"${compose[@]}" up --detach --no-build --wait --wait-timeout 120
docker image prune --force

echo "Deployed $IMAGE_REGISTRY/order-ingestion-*:$IMAGE_TAG"
