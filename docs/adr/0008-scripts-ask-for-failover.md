# Watch scripts ask for a failover

A known failure fails the VPN over to another region only when the watch script asks for it in its result
(`{ success: false, failureReason, failover: true }`), not whenever a watch with a setting turned on reports one. A
known failure is any problem the script recognised, and only some of them, such as a page that blocks the VPN's region,
would be fixed by another region: a changed login form or a sold-out item would not. The script is what knows which one
it found, so a per-watch setting would either fail over for problems a switch can't fix, pausing every VPN watch for
nothing, or make the Owner choose between failing over and recognising other problems at all.

## Consequences

- `failover` is part of the published watch SDK's result, so removing or renaming it would break watch scripts.
- A failover also moves every other watch that uses the VPN, since they share it; one script's request switches the
  region for all of them.
- A watch without `UseVpn` can still ask; Lookout records the request and does nothing else.
