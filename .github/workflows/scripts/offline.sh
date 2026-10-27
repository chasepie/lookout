#!/usr/bin/env bash
# Runs a command, such as a test suite, with no network but loopback, so a test can't reach a public site: one that
# tries fails instead. The command runs as the calling user, in a network namespace of its own, so the Actions runner
# itself stays online. Its environment is kept, and its children, such as Chromium and Script processes, are offline
# too. Linux only, and needs passwordless sudo, as GitHub's hosted runners have.
#
#   offline.sh npm run test:runner
set -euo pipefail

# sudo replaces PATH, and may replace HOME, where Playwright keeps its browsers, so they're passed on explicitly
exec sudo --preserve-env env "PATH=$PATH" "HOME=$HOME" unshare --net -- bash -euo pipefail -c '
  ip link set lo up

  # Prove the command is offline before trusting it to be: no route but loopback, and no public site. (A new namespace
  # can also hold tunnel devices the kernel creates, which are down and have no routes.)
  # Each listing is read on its own, so one that fails stops here rather than passing the check unseen, as it would
  # piped into grep under pipefail.
  ipv4_routes="$(ip -o -4 route show table all)"
  ipv6_routes="$(ip -o -6 route show table all)"
  if printf "%s\n%s\n" "$ipv4_routes" "$ipv6_routes" | grep -v -e " dev lo " -e "^$"; then
    echo "offline.sh: the namespace has a route besides loopback" >&2
    exit 1
  fi
  # A missing curl would fail the check below too, and pass it.
  command -v curl > /dev/null || { echo "offline.sh: curl is needed to check the namespace is offline" >&2; exit 1; }
  if curl --silent --max-time 5 --output /dev/null https://example.com/; then
    echo "offline.sh: reached https://example.com/ from inside the namespace" >&2
    exit 1
  fi

  exec setpriv --reuid="$1" --regid="$2" --init-groups -- "${@:3}"
' offline "$(id -u)" "$(id -g)" "$@"
