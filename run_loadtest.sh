#!/bin/bash
# Load test for TownSuite.WorkQueues.Server against Valkey, entirely in Docker, checked against
# TownSuite.WorkQueues.Server.LoadTest/baseline.json. Exits non-zero on a performance regression.
#
#   ./run_loadtest.sh                   # every scenario in the baseline
#   ./run_loadtest.sh --scenario steady # one scenario
#
# Writes build/loadtest/report.json. The server and Valkey each get 2 CPUs and 1 GB, so results
# are comparable between runs on the same machine.
set -e # exit on first error
set -u # exit on using unset variable

CURRENTPATH=$(pwd)
RUNID="wqload-${BUILD_TAG:-local}-$$"
# Docker names allow letters, digits, '_', '.' and '-' only. Bash substitution, not tr, so GNU and BSD agree.
RUNID="${RUNID//[^a-zA-Z0-9_.-]/-}"
NETWORK="$RUNID"
SERVER="$RUNID-server"
VALKEY="$RUNID-valkey"
IMAGE="workqueues-server:$RUNID"
SIGNING_KEY="load-test-signing-key-$(date +%s)-$$-padding-to-32-bytes"
REPORT_DIR="$CURRENTPATH/build/loadtest"
# A fresh output folder per run: Docker Desktop can keep a stale entry for a file deleted on the host.
RUN_DIR="$REPORT_DIR/$RUNID"

cleanup() {
    docker rm -f "$SERVER" "$VALKEY" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
    docker image rm "$IMAGE" >/dev/null 2>&1 || true
    rm -rf "$RUN_DIR"
}
trap cleanup EXIT

echo "Building the server image and the load test"
docker build -q -f TownSuite.WorkQueues.Server/Dockerfile -t "$IMAGE" . >/dev/null
rm -rf "$REPORT_DIR/app" "$REPORT_DIR/report.json"
mkdir -p "$RUN_DIR"
dotnet publish TownSuite.WorkQueues.Server.LoadTest/TownSuite.WorkQueues.Server.LoadTest.csproj \
    --configuration Release --output "$REPORT_DIR/app" -v q -nologo

echo "Starting Valkey and the server"
docker network create "$NETWORK" >/dev/null
docker run -d --name "$VALKEY" --network "$NETWORK" --cpus=2 --memory=1g valkey/valkey:8.1 >/dev/null
docker run -d --name "$SERVER" --network "$NETWORK" --cpus=2 --memory=1g \
    -e WorkQueuesServer__Backend=redis \
    -e WorkQueuesServer__ConnectionString="$VALKEY:6379" \
    -e WorkQueuesServer__RequireHttps=false \
    -e WorkQueuesServer__PollInterval=00:00:00.100 \
    -e WorkQueuesServer__Jwt__Issuer=https://auth.loadtest \
    -e WorkQueuesServer__Jwt__Audience=workqueues \
    -e WorkQueuesServer__Jwt__SigningKey="$SIGNING_KEY" \
    -e Logging__LogLevel__Default=Warning \
    "$IMAGE" >/dev/null

echo "Running the load test"
set +e
docker run --rm --network "$NETWORK" --cpus=4 \
    -e LOADTEST_SIGNING_KEY="$SIGNING_KEY" \
    -e DOTNET_DbgEnableMiniDump=0 \
    -v "$REPORT_DIR/app:/work/app:ro" \
    -v "$RUN_DIR:/work/out" \
    mcr.microsoft.com/dotnet/runtime:10.0 \
    dotnet /work/app/TownSuite.WorkQueues.Server.LoadTest.dll \
        --url "http://$SERVER:8080/" \
        --baseline /work/app/baseline.json \
        --report /work/out/report.json "$@"
STATUS=$?
set -e
if [ -f "$RUN_DIR/report.json" ]; then cp "$RUN_DIR/report.json" "$REPORT_DIR/report.json"; fi

if [ $STATUS -ne 0 ]; then
    echo "Server log (last 50 lines):"
    docker logs --tail 50 "$SERVER" 2>&1 || true
fi
exit $STATUS
