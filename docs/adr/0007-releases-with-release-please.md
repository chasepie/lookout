# Releases are cut by release-please, and are all that's published

Lookout publishes only releases: its images to GHCR and the watch SDK to GitHub Packages, at one shared version, from
one workflow (`.github/workflows/release.yml`). release-please picks the version from the Conventional Commit titles of
the PRs merged since the last release, and keeps a Release PR open with it and the changelog; merging that PR releases.
We chose it to keep releasing to standard tools and as little of our own logic as we can, and dropped what needed that
logic: builds of every commit to main (images tagged `main` and `sha-<commit>`, and a matching SDK channel). An npm
package can't move a dist-tag the way an image tag moves, so publishing every commit took a script to version each build
and keep the SDK's dist-tags from moving backwards when runs finished out of order. CI's image builds on every PR still
check the Dockerfiles.

## Consequences

- PR titles must be Conventional Commits (`.github/workflows/pr-title.yml` checks them), and must become the commits on
  main: main's ruleset allows only squash merges and requires the title check, and the repository's setting titles a
  squash merge with the PR's title. PRs into other branches can merge any way.
- Lookout is in initial development, at `0.x`: a breaking change bumps the minor version, and a feature the patch. A
  breaking change includes one that needs a new `docker-compose.yaml` or setting, so a deployment following a minor
  version's image tag, such as `0.4`, never moves to one; from 1.0, it follows the major, such as `1`.
- The SDK's version is Lookout's, even when the SDK didn't change, so a script's project installs the SDK of the release
  its Lookout runs. The Release PR sets it in `src/watch-sdk/package.json`, which stays `0.0.0` until the first release.
- release-please acts with a token of the Owner's (`RELEASE_PLEASE_TOKEN`), not `GITHUB_TOKEN`: GitHub runs no workflows
  for what `GITHUB_TOKEN` does, so its Release PRs would never run CI, which main's ruleset requires before merging.
- There are no builds of main to deploy between releases: to run a change, release it.
