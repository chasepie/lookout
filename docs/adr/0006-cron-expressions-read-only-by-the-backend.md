# Cron expressions are read only by the Backend

The frontend doesn't put a watch's cron expression into words, such as "At 09:00 AM, Monday through Friday". It shows
the expression as it is and, while the Owner types one, the next runs the Backend works out for it with Cronos, the
library the scheduler runs watches with (`GET /api/Cron/preview`). Browser libraries that describe cron, such as
cronstrue, read some expressions differently from Cronos: they treat a day of the month and a day of the week as either
one (Vixie cron), where Cronos needs both; they disagree about day 7 in ranges and steps; and they accept expressions
Cronos refuses. Each difference would show the Owner a schedule the watch doesn't keep, and they can't be patched one by
one until the two agree.

## Consequences

- The watch list and a watch's page show "Cron 0 9 * * 1-5", not a sentence; the page's next run time says when it
  runs.
- The form checks the expression on each change, so it needs the Backend to tell whether an expression is valid.
- The preview's times are shown in the Owner's time zone, while the expression's are in the server's, which the form
  names.
