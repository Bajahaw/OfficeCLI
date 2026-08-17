#!/bin/sh
set -eu

if [ -z "${OFFICECLI_MCP_TOKEN:-}" ]; then
  echo "officecli: OFFICECLI_MCP_TOKEN is required"
  echo "Set it in the Dokploy service environment, then redeploy."
  exit 1
fi

echo "officecli: starting MCP HTTP on 0.0.0.0:8765"
exec /app/officecli mcp --http --host 0.0.0.0 --port 8765 --workdir /data --token "$OFFICECLI_MCP_TOKEN"
