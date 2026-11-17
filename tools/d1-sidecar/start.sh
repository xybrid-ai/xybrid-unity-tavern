#!/usr/bin/env bash
# Starts the d1-2B decision model as a local llama.cpp sidecar for the table-reaction
# experiment. DEV ONLY: this runs outside Unity and outside the Xybrid runtime.
#
#   tools/d1-sidecar/start.sh            # foreground, port 8187
#   D1_PORT=8095 tools/d1-sidecar/start.sh
#
# Reuses the pinned runtime + weights that space-jarv's Tools/jarv-d1/bootstrap.py
# downloads (upstream llama.cpp b11960 macOS arm64 with Metal; LiquidAI/d1-2B-GGUF
# Q8_0 at revision e968f6575f0d4fad928312d7af15cad90fd365c0). Override with
# D1_LLAMA_SERVER / D1_GGUF. The GGUF is verified against its pinned SHA-256.
set -euo pipefail

JARV="${HOME}/Developer/Games/space-jarv/.context/d1"
SERVER="${D1_LLAMA_SERVER:-${JARV}/runtime/macos-arm64/llama-b11960/llama-server}"
GGUF="${D1_GGUF:-${JARV}/models/d1-2B-Q8_0.gguf}"
PORT="${D1_PORT:-8187}"
GGUF_SHA="073313d92b53b0ac8193db6e906cb3c78a98183d244b15b7b8f5426115c6738c"
STAMP="$(dirname "$0")/.verified-$(basename "$GGUF").sha256"

die() { echo "d1-sidecar: $*" >&2; exit 1; }

[ -x "$SERVER" ] || die "llama-server not found at $SERVER (run space-jarv Tools/jarv-d1/bootstrap.py or set D1_LLAMA_SERVER)"
[ -f "$GGUF" ] || die "d1 GGUF not found at $GGUF (set D1_GGUF)"

# Hashing 1.8 GB takes a few seconds; cache the result against size + mtime.
SIG="$(stat -f '%z %m' "$GGUF")"
if [ "$(cat "$STAMP" 2>/dev/null || true)" != "$SIG $GGUF_SHA" ]; then
  echo "d1-sidecar: verifying $GGUF ..."
  ACTUAL="$(shasum -a 256 "$GGUF" | cut -d' ' -f1)"
  [ "$ACTUAL" = "$GGUF_SHA" ] || die "checksum mismatch: $ACTUAL"
  echo "$SIG $GGUF_SHA" > "$STAMP"
fi

if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  die "port $PORT is already in use"
fi

echo "d1-sidecar: d1-2B Q8_0 on llama.cpp b11960 (Metal), http://127.0.0.1:${PORT}/v1/systemone"
exec "$SERVER" -m "$GGUF" --host 127.0.0.1 --port "$PORT" \
  -c 4096 -b 4096 -ub 4096 -ngl 99 -np 1 --no-webui
