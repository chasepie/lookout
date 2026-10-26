#!/usr/bin/env bash
# Publishes the scripting package's tarball, packed at a version from version.sh, under a dist-tag, for CD (see
# "Releasing" in README.md). Needs npm logged in to the package's registry, and runs nothing from the repository's
# dependencies, so a job that hasn't installed them can give it the token that publishes:
#   publish-scripting.sh <tarball> <dist-tag>
#
# A version that's already published is skipped, so a run can be re-run, as is an edge older than the last, so
# re-running an older run doesn't move edge back. A release publishes whether or not the types changed, so a Script's
# types match the Lookout it runs on. An edge is only published when what users would install differs from the last
# edge (its files, and its package.json but for the version and scripts), or the last edge was for an earlier
# release. Comparing what's published, rather than which files a push changed, ignores changes that don't reach
# users, and catches up after a run that was cancelled.
set -euo pipefail

tarball=$1
dist_tag=$2
work=$(mktemp -d)
mkdir "$work/new" "$work/old"
tar -xzf "$tarball" -C "$work/new"
name=$(node --print "require(process.argv[1]).name" "$work/new/package/package.json")
version=$(node --print "require(process.argv[1]).version" "$work/new/package/package.json")

# The version a spec resolves to, or nothing if none is published. Any error but E404, such as the registry not
# answering, stops the script, rather than being taken for nothing published. Only the version is read from npm's
# output, as it may warn on stderr even when it succeeds.
published() {
  local out
  if out=$(npm view "$1" version 2> "$work/view-error"); then
    echo "$out"
  elif ! grep --quiet E404 "$work/view-error"; then
    cat "$work/view-error" >&2
    return 1
  fi
}

# Assigned first: set -e ignores a failure inside a test's arguments
existing=$(published "$name@$version")
if [ -n "$existing" ]; then
  echo "$name@$version is already published"
  exit 0
fi

if [ "$dist_tag" = edge ]; then
  edge=$(published "$name@edge")
  if [ -n "$edge" ] && [ "$(printf '%s\n' "$edge" "$version" | sort -V | tail -n 1)" = "$edge" ]; then
    echo "$name@$edge is newer than $version"
    exit 0
  fi
  # Edges for the same release share everything before the run number
  if [ "${edge%.*}" = "${version%.*}" ]; then
    npm pack "$name@$edge" --pack-destination "$work/old"
    tar -xzf "$work"/old/*.tgz -C "$work/old"
    manifest() {
      node --print "const { version, scripts, ...rest } = require(process.argv[1]); JSON.stringify(rest)" \
        "$1/package/package.json"
    }
    if diff --recursive --exclude=package.json "$work/old/package" "$work/new/package" \
      && [ "$(manifest "$work/old")" = "$(manifest "$work/new")" ]; then
      echo "$name@$edge already has these types"
      exit 0
    fi
  fi
fi

npm publish "$tarball" --tag "$dist_tag"
