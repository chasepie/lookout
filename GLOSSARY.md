# Lookout

Lookout runs watch scripts against web pages, on demand or on a schedule, and keeps the result of every run.

## Language

### People

**Owner**:
The one person who signs in to Lookout. Every watch, run and VPN setting is the Owner's; there are no other users.
_Avoid_: User, admin, account

### Watching

**Watch**:
Something the Owner wants checked: a watch script, with when and how to run it.

**Watch script**:
The code a watch runs against a web page in a browser, which ends by returning a result or by throwing.
_Avoid_: Script file, job

**Run**:
One execution of a watch's script, and the record Lookout keeps of it.
_Avoid_: Job, execution, check

**Success**:
A result in which the watch script reports what it found on the page, as its content.
_Avoid_: Observation, pass

**Known failure**:
A result in which the watch script reports that it recognised a problem, such as a page that a VPN region is blocked
from, and why. It can ask for a failover.
_Avoid_: Failed run, failure, error

**Crash**:
How a run ends when it has no result: the watch script threw, timed out or was cancelled, or couldn't be started.
_Avoid_: Failed run, failure, error

**Screenshot**:
The one image a run keeps of a page: either the one the watch script returned with its result, or a full-page image
Lookout took of the page given to the script, when the watch opted into one for how the run ended.
_Avoid_: Capture, snapshot

**Content change**:
A success whose content differs from the content of the watch's previous success. A watch's first success is not one.

**Known-failure streak**:
A watch's known failures in a row, with no success between them. Crashes neither add to it nor end it.

**Crash streak**:
A watch's crashes in a row. Any run that isn't a crash ends it.

**Recovery**:
A watch's first success after a known-failure streak that reached the watch's threshold while it ran, or after any crash
streak, when it is not also a content change.
_Avoid_: Stable again, back to normal, resolved

### VPN

**Region**:
Where the VPN connects from, among those its provider offers. A run of a watch that uses the VPN runs in one region.
_Avoid_: Location, server, country

**Region switch**:
A change of the VPN's region, which waits for any run using the VPN to finish, and which runs using the VPN wait for.
_Avoid_: Rotation (for switches in general), reconnect

**Rotation**:
A region switch made on a timer, to a region the Owner hasn't excluded.

**Failover**:
A region switch made because a run ended in a known failure that asked for one, to a region the Owner hasn't excluded
and the watch hasn't already failed in during its known-failure streak.
_Avoid_: Fallback, escape

**Retry**:
A run Lookout queues after a failover, to try the watch again in the new region.
_Avoid_: Rerun, re-check

### Notifying

**Notification**:
A message Lookout sends to the Owner, through a notification channel, about something a run showed.
_Avoid_: Alert, push, message

**Notification channel**:
A place the Owner has set up for notifications to go, such as a Pushover account, which any number of watches can send
to. Each uses one service, such as Pushover or Discord.
_Avoid_: Channel (alone), destination, target, notifier

**Event**:
Something a run showed that the Owner can be notified about: a content change, a known-failure streak reaching the
watch's threshold, the start of a crash streak, or a recovery.
_Avoid_: Trigger, alert

**Subscription**:
A watch's choice to send some of its events to a notification channel, and how.
_Avoid_: Rule, route
