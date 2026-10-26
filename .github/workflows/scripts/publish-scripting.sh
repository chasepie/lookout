#!/usr/bin/env bash
# Publishes the scripting package at a version under a dist-tag, from version.sh, for CI (see "Releasing" in
# README.md). Run from the repository root, with dependencies installed and npm logged in to the package's registry:
#   publish-scripting.sh <version> <dist-tag>
#
# A release publishes whether or not the types changed, so a Script's types match the Lookout it runs on, and is
# skipped if its version is already published, so a release's run can be re-run. An edge is only published when what
# users would install differs from the last edge (its files, and its package.json but for the version and scripts),
# or the last edge was for an earlier release. Comparing what's published, rather than which files a push changed,
# ignores changes that don't reach users, and catches up after a run that was cancelled.
set -euo pipefail

version=$1
dist_tag=$2
dir=src/scripting
name=$(node --print "require('./$dir/package.json').name")

# The version a spec resolves to, or nothing if none is published. Any error but E404, such as the registry not
# answering, stops the script, rather than being taken for nothing published.
published() {
  local out
  if out=$(npm view "$1" version 2>&1); then
    echo "$out"
  elif ! grep --quiet E404 <<< "$out"; then
    echo "$out" >&2
    return 1
  fi
}

if [ "$dist_tag" != edge ]; then
  # Assigned first: set -e ignores a failure inside a test's arguments
  release=$(published "$name@$version")
  if [ -n "$release" ]; then
    echo "$name@$version is already published"
    exit 0
  fi
fi

work=$(mktemp -d)
npm pkg set version="$version" --workspace "$dir"
npm pack --workspace "$dir" --pack-destination "$work"
tarball=$(echo "$work"/*.tgz)

if [ "$dist_tag" = edge ]; then
  edge=$(published "$name@edge")
  # Edges for the same release share everything before the run number
  if [ "${edge%.*}" = "${version%.*}" ]; then
    mkdir "$work/new" "$work/old"
    tar -xzf "$tarball" -C "$work/new"
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
