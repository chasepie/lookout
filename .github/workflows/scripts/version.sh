#!/usr/bin/env bash
# Works out the version a push publishes, for CI (see "Releasing" in README.md), and writes it to $GITHUB_OUTPUT as
# version, with npm-tag, the dist-tag the scripting package is published under. Needs every tag and main fetched.
#
# A release tag publishes its own version, and must be one, such as v0.2.0 or v0.2.0-rc.1, on main: any other tag
# would replace latest with whatever it points at. A push to main publishes the next patch after the latest release,
# such as 0.2.1-edge.<run number>, which a later release sorts above. Before the first release, the next patch is the
# root package.json's version.
set -euo pipefail

if [[ $GITHUB_REF == refs/tags/* ]]; then
  if ! [[ $GITHUB_REF_NAME =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$ ]]; then
    echo "::error::$GITHUB_REF_NAME isn't a release version, such as v0.2.0 or v0.2.0-rc.1"
    exit 1
  fi
  if ! git merge-base --is-ancestor "$GITHUB_SHA" origin/main; then
    echo "::error::$GITHUB_REF_NAME isn't on main"
    exit 1
  fi
  version=${GITHUB_REF_NAME#v}
  # npm publishes a pre-release only under a dist-tag other than latest
  if [[ $version == *-* ]]; then npm_tag=next; else npm_tag=latest; fi
else
  release=$(git tag --list 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -n 1) || true
  if [ -n "$release" ]; then
    IFS=. read -r major minor patch <<< "${release#v}"
    base="$major.$minor.$((patch + 1))"
  else
    base=$(node --print "require('./package.json').version")
  fi
  version="$base-edge.$GITHUB_RUN_NUMBER"
  npm_tag=edge
fi

echo "Publishing $version, the scripting package tagged $npm_tag"
{
  echo "version=$version"
  echo "npm-tag=$npm_tag"
} >> "$GITHUB_OUTPUT"
