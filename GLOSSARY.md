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
from, and why.
_Avoid_: Failed run, failure, error

**Crash**:
How a run ends when it has no result: the watch script threw, timed out or was cancelled, or couldn't be started.
_Avoid_: Failed run, failure, error

**Screenshot**:
The one image a run keeps of a page: either the one the watch script returned with its result, or a full-page image
Lookout took of the page given to the script, when the watch opted into one for how the run ended.
_Avoid_: Capture, snapshot
