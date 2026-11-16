# One loop schedules runs and region switches

A region switch restarts the VPN tunnel, which would cut off a run using it, so runs that use the VPN and switches must
take turns. `WatchRunWorker`'s loop decides both: each turn it plans the switch that's due, makes the planned one in the
background once no VPN run is in progress, and starts pending runs, starting no VPN run while a switch is planned or in
progress. `Vpn` has no loop of its own. It knows gluetun, the regions and the VPN settings, says which switch is due,
picks its region and makes it, but schedules nothing.

They used to be two background services that coordinated with each other. The run worker started runs. `Vpn`'s own
loop made switches. Between them were a gate, which many VPN runs could hold at once but a switch needed to itself, an
event telling the worker that runs might start again, and a queue for the switches runs asked for, which kept only the
latest. A run that found the VPN in a region its watch avoids could only ask for a switch and look again later. Its
request might be dropped, or overtaken by another switch, so every switch rechecked whether it was still needed. Most of
that code existed only because the service that saw the run couldn't make the switch. With one loop owning the plan,
the switch in progress and the counts of runs in progress, a run in an avoided region plans the switch itself, and the
only change a plan must survive while it waits is one the Owner makes.

## Consequences

- Only the loop changes the region, so a VPN run records the region the loop read as it started. A switch made for the
  check of gluetun needs no recheck. A rotation is checked once more before it's made, since the Owner may have turned
  rotation off, and so is a switch for a watch, since the Owner may have cleared the region.
- A switch waits only for the VPN runs already in progress. Runs that don't use the VPN keep starting while it waits
  and while it's made.
- The loop checks gluetun once a minute itself, so a gluetun that's slow to answer holds up the start of every run, not
  only VPN ones, for as long as its requests take to time out.
