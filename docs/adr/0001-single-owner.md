# One Owner, no per-user data

Lookout has exactly one person who signs in, the Owner, and watches, runs and VPN settings have no owner or user ID: signing in only answers "is this the
Owner?". We chose this over several accounts, shared or each with their own watches, because a watch script is arbitrary code that the Runner executes on its
host, so anyone allowed to upload one can already read every other user's data and secrets. A second account could not really be limited, and modelling
ownership would suggest an isolation that does not exist.

## Consequences

- The updates hub broadcasts every change to every connection, and no query filters by user.
- Supporting several users would first need watch scripts sandboxed away from the host and from each other, and then an owner on watches and runs, filtering in
  every query and hub group, and the VPN settings made admin-only.
