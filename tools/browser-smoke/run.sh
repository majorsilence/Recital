#!/bin/sh
# Publishes the browser head, serves it, and loads it in headless Firefox: prints the transfer size, how
# long the runtime took to start, what it drew into, and saves a screenshot (GAP-014). Needs the
# wasm-tools workload and Firefox. Usage: tools/browser-smoke/run.sh [screenshot.png]
set -e
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
shot=${1:-$work/shot.png}
dotnet publish "$repo/hosts/Majorsilence.Recital.Browser" -c Release -o "$work/publish" >/dev/null
cp -r "$work/publish/wwwroot" "$work/site"
cp "$here/probe.html" "$work/site/index.html"
python3 -c "
import os, sys
raw = br = 0
for d, _, fs in os.walk(sys.argv[1]):
    for f in fs:
        p = os.path.join(d, f)
        if f.endswith('.br'): br += os.path.getsize(p)
        elif not f.endswith('.gz'): raw += os.path.getsize(p)
print(f'bundle: {raw/1e6:.1f} MB, {br/1e6:.1f} MB with brotli')" "$work/site"
python3 -I "$here/serve.py" "$work/site" 8765 > "$work/server.log" 2>&1 &
server=$!
# Firefox from snap only reads profiles under ~/snap/firefox.
profile="$HOME/snap/firefox/common/recital-smoke"
rm -rf "$profile"; mkdir -p "$profile"
printf 'user_pref("devtools.console.stdout.content", true);\nuser_pref("dom.max_script_run_time", 0);\n' > "$profile/user.js"
timeout 200 firefox --headless --no-remote --profile "$profile" --window-size=1000,700 --screenshot "$profile/shot.png" http://127.0.0.1:8765/index.html 2>&1 | grep -E "JavaScript (error|warning)" || true
kill $server
python3 -c "import urllib.parse, sys; print(urllib.parse.unquote(open(sys.argv[1]).read()))" "$work/server.log"
cp "$profile/shot.png" "$shot" && echo "screenshot: $shot"
