# Lookout

Lookout is a self-hosted app that watches webpages and tells its user when something they care about on a page changes, such as a price or stock status.

## Language

**Watch**:
Something the user is keeping an eye on: how to read a page, when to read it, and the history of what was read.
_Avoid_: Scraper, Monitor, Tracker, Job

**Script**:
User-written code that drives a browser to read a page for a Watch, reporting whether it found what it was looking for and the content it found.
_Avoid_: Scraper, Scraper script

**Script version**:
One uploaded revision of a Watch's Script. Every Run records the Script version it ran.
_Avoid_: Revision, Upload

**Rotation**:
Switching the VPN to a new exit IP, either on a timer or early after a Known failure.
_Avoid_: Reconnect, IP change

**Notification URL**:
The page a Watch's notifications link to, for the user to open. It is not necessarily where the Script navigates.
_Avoid_: Target URL, Watch URL

**Run**:
One execution of a Watch, whether scheduled or on demand, recorded in the Watch's history.
_Avoid_: Scrape, Check, Execution

**VPN Run**:
A Run of a Watch that uses the VPN, whose browser goes out through the VPN's exit IP. It records the region and exit IP it went through, and is refused unless the VPN is connected.
_Avoid_: Proxied Run

**Direct Run**:
A Run of a Watch that doesn't use the VPN, whose browser goes out from the homelab's own IP.
_Avoid_: Plain Run, Non-VPN Run

**Test run**:
An execution of a Script to show its result to the user. It is not recorded in the Watch's history and never affects its Baseline.
_Avoid_: Dry run, Preview

**Channel**:
A destination Lookout sends notifications to, such as a Discord webhook or a Pushover account, configured once and chosen per Watch.
_Avoid_: Integration, Notifier

**Change**:
A difference between the content of a Watch's latest Succeeded Run and that of its previous Succeeded Run, as judged by Lookout rather than by the Script.
_Avoid_: Diff, Update

**Baseline**:
The content of a Watch's most recent Succeeded Run, which the next Succeeded Run is compared against.
_Avoid_: Last value, Previous result

**Account**:
The single local login that protects Lookout, created by first-launch setup.
_Avoid_: User, Admin

**Session**:
One login to the Account, lasting from logging in until logging out.
_Avoid_: Token

### Watch statuses

**Pending**:
The Watch has no Baseline yet, because it is new or a new Script version was just uploaded. A Watch in a failure streak is Failing instead, even without a Baseline.
_Avoid_: New, Unknown

**Healthy**:
The Watch's latest Run Succeeded.
_Avoid_: Succeeded, OK, Active

**Failing**:
The Watch is in a streak of Known failure or Errored Runs.
_Avoid_: Broken, Erroring

**Paused**:
The user has stopped the Watch's scheduled Runs; its Baseline and failure streak are kept.
_Avoid_: Disabled, Stopped

**Running**:
A Run of the Watch is in progress. It is shown alongside the Watch's status rather than replacing it; Test runs do not count.
_Avoid_: In progress, Busy

### Run outcomes

**Succeeded**:
The Script found what it was looking for and reported its content.
_Avoid_: Passed, OK

**Known failure**:
The Script recognised a situation its author anticipated, such as an "Access Denied" page, and reported that it could not get the content.
_Avoid_: Failed, Blocked

**Errored**:
The Run ended in a way the Script's author did not anticipate, such as an unhandled exception, a missing selector, or a timeout.
_Avoid_: Failed, Crashed
