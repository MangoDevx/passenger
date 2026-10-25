#!/bin/sh
# Passenger as an MCP server over a folder (the current one by default), for an agent to start: `just mcp`.
# It runs from a clone of the last Debug build, so building Passenger while an agent uses it never changes a
# file under the running IDE. The clone is APFS's (or a reflink), so it costs no space, and it goes when the
# server does. Standard output is the protocol's.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
build="$root/artifacts/bin/Passenger.App/debug"
if [ ! -x "$build/Passenger" ]; then
    echo "passenger: no build at $build; run just build first" >&2
    exit 1
fi
copies="$root/artifacts/mcp"
mkdir -p "$copies"
# A copy named for a shell no longer running is left from a server that was killed.
for old in "$copies"/*; do
    [ -d "$old" ] || continue
    kill -0 "$(basename "$old")" 2>/dev/null || rm -rf "$old"
done
copy="$copies/$$"
if [ "$(uname)" = Darwin ]; then
    cp -cR "$build" "$copy"
else
    cp -R --reflink=auto "$build" "$copy"
fi
trap 'rm -rf "$copy"' EXIT
trap 'exit 1' INT TERM HUP
"$copy/Passenger" --mcp "${1:-$PWD}"
