#!/bin/bash
# Analyzes and tests the package.
set -uo pipefail
cd "$(dirname "$0")/.."

failures=0

if dart analyze --fatal-infos > /tmp/orblit_net_analyze.log 2>&1; then
  echo "  ok    analyze"
else
  echo "  FAIL  analyze"; tail -20 /tmp/orblit_net_analyze.log; failures=$((failures+1))
fi

if dart test > /tmp/orblit_net_test.log 2>&1; then
  summary=$(tr '\r' '\n' < /tmp/orblit_net_test.log | tail -1 \
    | sed -e 's/\x1b\[[0-9;]*m//g' -e 's/^[0-9:]* //')
  echo "  ok    $summary"
else
  echo "  FAIL  tests"; tail -25 /tmp/orblit_net_test.log; failures=$((failures+1))
fi

# The transport test is the one test that runs in a browser. It needs Chrome,
# and only a run outside CI may go without.
has_chrome() {
  [ -n "${CHROME_EXECUTABLE:-}" ] || [ -d "/Applications/Google Chrome.app" ] \
    || command -v google-chrome > /dev/null || command -v chromium > /dev/null
}
if [ -z "${CI:-}" ] && ! has_chrome; then
  echo "  skip  web transport (no Chrome found)"
elif { dart test -p chrome test/socket_transport_test.dart \
  && dart test -p chrome -c dart2wasm test/socket_transport_test.dart; } \
  > /tmp/orblit_net_web.log 2>&1; then
  echo "  ok    web transport"
else
  echo "  FAIL  web transport"; tail -25 /tmp/orblit_net_web.log; failures=$((failures+1))
fi

if dart format --output=none --set-exit-if-changed lib test bin > /dev/null 2>&1; then
  echo "  ok    format"
else
  echo "  FAIL  format"; failures=$((failures+1))
fi

echo
[ "$failures" -eq 0 ] && echo "everything green" || echo "$failures failing step(s)"
exit "$failures"
