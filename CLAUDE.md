# Lookout

## Agent skills

### Issue tracker

Issues and specs are tracked in GitHub Issues for chasepie/lookout, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Uses the five default triage labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Conventions

Write TypeScript (`.ts`) for all Node code: packages, tests, test fixtures and examples. The one exception is a `.js` Script fixture whose purpose is testing that the runner accepts JavaScript Scripts.
