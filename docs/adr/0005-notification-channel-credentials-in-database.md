# Notification channel credentials live in the database

A notification channel's credentials, such as a Pushover application token and user key, are stored in the database
with the channel, entered through the UI, rather than in the deployment's `.env` like every other secret
([ADR 0004](0004-production-secrets-in-env.md)). Notification channels are data the Owner creates, edits and deletes at
any time, any number of them, each with its own credentials, so they can't be a fixed list of Compose variables without
a redeploy for every change. The database is already as sensitive as `.env`: it sits beside the Data Protection keys,
and whoever can read one can read the other.

## Consequences

- The API takes credentials but never returns them: the UI leaves their fields blank when editing a channel, and blank
  keeps the saved ones.
- Backups of the database carry the credentials, so they need the same care as `.env`.
