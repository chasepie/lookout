# Watches avoid regions instead of failing the VPN over

A known failure that asks for it (`{ success: false, failureReason, avoidRegion: true }`) makes the region its run ran
in an avoided region of that watch, kept until the Owner clears it. It used to fail the VPN over: the run queued a
region switch before it ended, which kept every VPN run out of the region until the switch was done, and the switch
avoided only the regions the watch had failed in since its last success. Now nothing happens until the watch's next run
is about to start: if the VPN is in one of the watch's avoided regions, the run waits for a region switch away from it,
and other watches keep running in the region meanwhile, since a region bad for one site says nothing about another.
Carrying a failover from the run that asked for it to the switch took a lease that outlived the run, a switch queued
before it could wait for its turn, and a background task per failover; checking what a run is about to do needs none of
them, and a check that's missed is made again at the next poll.

## Considered options

- **Avoid regions only until the watch's next success**, worked out from its runs, which needs no new table. Rejected: a
  script may avoid a region that's flaky rather than blocked, and a success elsewhere says nothing about whether it's
  better now.
- **Avoid regions for a fixed time.** Left for later; it needs a time the Owner would have to pick.
- **Run anyway in the current region when the watch avoids every region it may switch to.** Rejected: the watch asked to
  avoid that region, and Lookout shouldn't assume the Owner is happy for it to run there again. The run crashes instead,
  which tells the Owner the watch can't run until they clear a region.

## Consequences

- The SDK's `failover` is now `avoidRegion`, which breaks watch scripts that set it.
- A watch that avoids every region allowed by `GLUETUN_SERVER_REGIONS` and not excluded by the Owner crashes on each run
  until the Owner clears one. Its retry is queued anyway, and crashes too.
- A region switch made for a watch prefers a region that no other watch with a pending VPN run avoids, and rotation one
  that no watch using the VPN avoids, so watches avoiding different regions don't switch the VPN back and forth.
- VPN runs wait for the Backend's first region switch, so every one knows its region.
- Runs that asked for a failover before this change don't make their regions avoided.
