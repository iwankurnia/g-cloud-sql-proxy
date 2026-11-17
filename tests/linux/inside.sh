#!/bin/sh
# Runs inside the test container (see run.ps1): controller tests on Linux, then
# the window on a virtual display, driven with xdotool and screenshotted.
set -u
out=/work/out
mkdir -p "$out"
fail=0
check() { if [ "$1" = "0" ]; then echo "  ok    $2"; else echo "  FAIL  $2"; fail=$((fail+1)); fi; }

echo "== controller tests"
cp /work/tests/fakeproxy /work/tests/ControllerTests /tmp/ 2>/dev/null || true
chmod +x /work/tests/ControllerTests /work/tests/fakeproxy
( cd /work/tests && ./ControllerTests ) > "$out/controller-tests.log" 2>&1
check $? "controller tests on Linux ($(tail -1 "$out/controller-tests.log"))"

echo "== window on Xvfb"
Xvfb :99 -screen 0 1280x900x24 -nolisten tcp >/dev/null 2>&1 &
sleep 1
export HOME=/tmp/home; mkdir -p "$HOME/.config/CloudSqlProxyManager"
export CLOUDSQLPROXY_EXE=/work/tests/fakeproxy
# A saved connection, so Enter (the default button, Connect) has something to connect to.
# The window must stay visible for the screenshots, so hide-on-connect is off here.
cat > "$HOME/.config/CloudSqlProxyManager/CloudSqlProxyManager.settings.json" <<'EOF'
{"ProxyPath":"","AutoReconnect":true,"HideOnConnect":false,"Connections":[
 {"Project":"my-project","Region":"asia-southeast2","Instance":"orders-db-replica-1","Port":3306,"CredentialsFile":"","ExtraArgs":""}]}
EOF
chmod +x /work/app/CloudSqlProxyManager
/work/app/CloudSqlProxyManager > "$out/app.log" 2>&1 &
app=$!
# wait for the window
for i in $(seq 1 60); do
    wid=$(xdotool search --name 'Cloud SQL Proxy Manager' 2>/dev/null | head -1)
    [ -n "$wid" ] && break
    sleep 0.5
done
check "$([ -n "$wid" ] && echo 0 || echo 1)" "window appeared (id $wid)"
[ -z "$wid" ] && { kill $app; cat "$out/app.log"; exit 1; }
sleep 2
import -window root "$out/1-linux-idle.png"
xdotool windowmove "$wid" 0 0 2>/dev/null
geo=$(xdotool getwindowgeometry "$wid")
echo "  window: $(echo "$geo" | tr '\n' ' ')"

# Connect: the button sits at the right of the status band. Press Enter instead:
# the Connect button is the window's default button.
xdotool windowactivate --sync "$wid" 2>/dev/null; xdotool key --window "$wid" Return
sleep 3
import -window root "$out/2-linux-connected.png"
title=$(xdotool getwindowname "$wid")
echo "  title: $title"
check "$(echo "$title" | grep -q 'Connected' && echo 0 || echo 1)" "Connect -> Connected (title shows it)"
pids=$(pgrep -x fakeproxy | wc -l)
check "$([ "$pids" = "1" ] && echo 0 || echo 1)" "one proxy process running ($pids)"

# Crash the proxy: it must come back by itself
pkill -x fakeproxy
sleep 1
import -window root "$out/3-linux-reconnecting.png"
sleep 4
title=$(xdotool getwindowname "$wid")
check "$(echo "$title" | grep -q 'Connected' && echo 0 || echo 1)" "back to Connected after the proxy was killed ($title)"
import -window root "$out/4-linux-reconnected.png"

# Settings were re-saved by Connect (the file gets a timestamp newer than the seed)
check "$(grep -q '"HideOnConnect": false' "$HOME/.config/CloudSqlProxyManager/CloudSqlProxyManager.settings.json" && echo 0 || echo 1)" "settings re-saved under ~/.config"

# Killing the app must not leave its proxy behind
kill $app
sleep 2
pids=$(pgrep -x fakeproxy | wc -l)
check "$([ "$pids" = "0" ] && echo 0 || echo 1)" "closing the app ends its proxy ($pids left)"

# Fresh profile: the first run offers the known project with its region filled in
rm -rf "$HOME/.config/CloudSqlProxyManager"
/work/app/CloudSqlProxyManager > "$out/app-fresh.log" 2>&1 &
app=$!
for i in $(seq 1 60); do
    wid=$(xdotool search --name 'Cloud SQL Proxy Manager' 2>/dev/null | head -1)
    [ -n "$wid" ] && break
    sleep 0.5
done
sleep 2
xdotool windowmove "$wid" 0 0 2>/dev/null; sleep 0.5
import -window root "$out/5-linux-first-run.png"
kill $app

echo
echo "$fail failed"
exit $fail
