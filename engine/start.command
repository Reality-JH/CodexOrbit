#!/bin/sh
# macOS / Linux launcher for orbit-core.
# Double-click or run: ./start.command
DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
CFG="$HOME/.codexorbit/config.json"

BIN="$DIR/orbit-core"
[ -x "$BIN" ] || BIN="$DIR/dist/orbit-core-darwin-arm64"
[ -x "$BIN" ] || BIN="$DIR/dist/orbit-core-darwin-amd64"
if [ ! -x "$BIN" ]; then
  printf '%s\n' "找不到 orbit-core 可执行文件，请先运行 ./build.sh" >&2
  exit 1
fi

if [ ! -f "$CFG" ]; then
  printf '%s\n' "首次运行：请先配置订阅链接，例如"
  printf '  %s sub add "https://你的订阅链接"\n' "$BIN"
  "$BIN" init --config "$CFG"
fi

printf '%s\n' "启动 orbit-core（不修改系统代理，也不影响本地 Clash）。按 Ctrl+C 停止并还原 Codex 配置。"
exec "$BIN" serve --config "$CFG"
