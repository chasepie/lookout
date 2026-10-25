#!/usr/bin/env bash
# Publishes the scripting package as an edge build, tagged edge, for CI on a push to main (see "Releasing" in
# README.md). Run from the repository root, with dependencies installed and npm logged in to the package's registry.
#
# Only publishes when what users would install differs from the last edge, or the last release before the first
# edge: its files, and the package.json fields that resolve them and name its dependencies. Comparing what's
# published, rather than which files a push changed, ignores changes that don't reach users, and catches up after a
# run that was cancelled. The version is the next patch after the last release, such as 0.2.1-edge.<run number>.
set -euo pipefail

dir=src/scripting
name=$(node --print "require('./$dir/package.json').name")
work=$(mktemp -d)
mkdir "$work/new" "$work/old"
npm pack --workspace "$dir" --pack-destination "$work/new"
tar -xzf "$work"/new/*.tgz -C "$work/new"

published=$(npm view "$name@edge" version 2>/dev/null) || published=$(npm view "$name@latest" version 2>/dev/null) || true
if [ -n "$published" ]; then
  npm pack "$name@$published" --pack-destination "$work/old"
  tar -xzf "$work"/old/*.tgz -C "$work/old"
  fields() {
    node --print "const p = require(process.argv[1]);
      JSON.stringify([p.types, p.exports, p.dependencies, p.peerDependencies, p.peerDependenciesMeta])" \
      "$1/package/package.json"
  }
  if diff --recursive --exclude=package.json "$work/old/package" "$work/new/package" \
    && [ "$(fields "$work/old")" = "$(fields "$work/new")" ]; then
    echo "$name@$published already has these types"
    exit 0
  fi
fi

# Before the first release, package.json's version. Until then, latest is the first edge, as a registry tags a
# package's first version latest whatever tag it was published with.
latest=$(npm view "$name@latest" version 2>/dev/null) || true
if [ -n "$latest" ] && [[ $latest != *-* ]]; then
  base=$(node --print "const [major, minor, patch] = process.argv[1].split('.');
    [major, minor, Number(patch) + 1].join('.')" "$latest")
else
  base=$(node --print "require('./$dir/package.json').version")
fi
npm pkg set version="$base-edge.$GITHUB_RUN_NUMBER" --workspace "$dir"
npm publish --workspace "$dir" --tag edge
