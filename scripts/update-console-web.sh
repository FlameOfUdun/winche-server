#!/usr/bin/env bash
# Builds the console web app (the winche-console repository) and puts its files where the server embeds them,
# recording which commit they came from. Run it after changing the web app, then commit what it changed here.
#
#   scripts/update-console-web.sh [path to winche-console]   (default: ../winche-console next to this repository)
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
web_repo="$(cd "${1:-$here/../winche-console}" && pwd)"
out="$here/src/Winche.Console/wwwroot"

cd "$web_repo"
[ -d node_modules ] || npm ci
npm run build -- --outDir "$out" --emptyOutDir

commit="$(git -C "$web_repo" rev-parse --short HEAD)"
dirty=""; [ -n "$(git -C "$web_repo" status --porcelain)" ] && dirty=" (with uncommitted changes)"
cat > "$here/src/Winche.Console/console-web.json" <<JSON
{
  "repository": "winche-console",
  "commit": "$commit",
  "note": "built from $commit$dirty"
}
JSON
echo "Console web app from winche-console $commit$dirty → src/Winche.Console/wwwroot"
