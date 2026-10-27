#!/usr/bin/env bash
# Scale-out demo: hard-kills one server instance, like a crash (macOS and Linux; needs lsof).
# server-a is port 5201 and server-b is port 5202. Bring it back with Start on its row in the Aspire
# dashboard, or by re-running its launch profile.
#
#   ./scripts/kill-instance.sh server-a
#   ./scripts/kill-instance.sh 5202
set -euo pipefail

case "${1:-server-a}" in
    server-a) port=5201 ;;
    server-b) port=5202 ;;
    *[!0-9]*) echo "usage: $0 [server-a|server-b|PORT]" >&2; exit 2 ;;
    *) port=$1 ;;
esac

pids=$(lsof -nP -t -iTCP:"$port" -sTCP:LISTEN || true)
if [ -z "$pids" ]; then
    echo "Nothing is listening on port $port."
    exit 1
fi

killed=0
for pid in $pids; do
    name=$(ps -p "$pid" -o comm= | xargs basename)
    # Only this sample's own processes: a wrong port must not take down something else.
    case "$name" in
        Mcp2Dialog.*|dotnet) ;;
        *) echo "Not killing $name (pid $pid) on port $port: it isn't part of this sample."; continue ;;
    esac
    kill -9 "$pid"
    echo "Killed $name (pid $pid), which was listening on port $port."
    killed=$((killed + 1))
done

[ "$killed" -gt 0 ]
