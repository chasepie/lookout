# The watch SDK is published in step with Lookout

The watch SDK is published to GitHub Packages so watch scripts can be written in other projects, but Lookout never
installs it from there: the Runner image builds the SDK from its own commit, and every script runs against that. So the
published SDK's versions are Lookout's, not its own. CD publishes it from the same pushes and tags as the images, and a
script's project picks the SDK that matches the image it runs on: `main` for `main`, `1.4.0` for `1.4.0`, and a main build
for `sha-<commit>`. A version of its own would need a table of which SDK works with which Lookout, and would be bumped by
hand, when its version only ever answers that question.

## Consequences

- A release publishes a new SDK version even when the SDK didn't change.
- `package.json` stays at `0.0.0`. CD sets the version when it publishes, from the tag or, for main, as
  `0.0.0-main.<commit time>.sha-<commit>`, so main builds sort below every release and a range like `^1.2.0` never picks
  one up.
- npm versions can't be replaced and every publish sets a dist-tag, so the SDK can't follow the images exactly: a re-run
  of an older main commit publishes nothing, rather than a `sha-` build as the images do, and releases that don't become
  `latest` go under `next` (pre-releases) or `release-<major>.<minor>` (older releases).
- GitHub Packages, rather than npmjs.com, keeps the package with the repo and its `GITHUB_TOKEN`, at the cost of a
  `read:packages` token in every project that installs it, even though the package is public.
