# .NET orchestrates Runs; Node executes Scripts

The .NET backend owns scheduling, storage, Change detection and notifications, and never drives a browser itself. Scripts are written by users against Node's Playwright `Page` API, so a Node.js runner executes them and reports each outcome back to the backend. Scripts are stateless: they report what they found, and only the backend decides whether that is a Change, by comparing against the Baseline it stored.

## Considered Options

- **Drive the browser from .NET with `Microsoft.Playwright`.** Rejected because it can't run user-written TypeScript or JavaScript Scripts, which is the whole point. This is why the early `ScrapeController` scaffolding and the `Microsoft.Playwright` package are removed.
- **Let Scripts detect Changes themselves.** Rejected because Scripts would then need access to their own history, which makes them harder to write, test and sandbox.
