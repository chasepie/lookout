#!/usr/bin/env bash
# Works out the version a push publishes, for CD (see "Releasing" in README.md), and writes it to $GITHUB_OUTPUT as
# version, with npm-tag, the dist-tag the scripting package is published under. Needs every tag and main fetched.
#
# A release tag publishes its own version. It must be a SemVer version, such as v0.2.0 or v0.2.0-rc.1, on main, and
# the newest release: an older one would move latest back. A push to main publishes the next patch after the latest
# release it contains, such as 0.2.1-edge.<run number>, which a later release sorts above. Before the first release,
# the next patch is the root package.json's version.
set -euo pipefail
# So a failure inside $(…), such as git's, stops the script too
shopt -s inherit_errexit

# SemVer 2.0.0, without build metadata: no leading zeros, and no empty pre-release identifiers
core='(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
identifier='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
prerelease="-$identifier(\.$identifier)*"

# The newest release, without pre-releases, among the tags merged into a commit, or nothing before the first
newest_release() {
  local tags
  tags=$(git tag --list 'v*' --merged "$1")
  grep -E "^v$core\$" <<< "$tags" | sort -V | tail -n 1 || true
}

# A shallow clone would have too few tags, so its edges would be numbered as if there were no release
if [ "$(git rev-parse --is-shallow-repository)" = true ]; then
  echo "::error::Needs the whole history, with every tag: check out with fetch-depth: 0"
  exit 1
fi

if [[ $GITHUB_REF == refs/tags/* ]]; then
  tag=$GITHUB_REF_NAME
  if ! [[ $tag =~ ^v$core($prerelease)?$ ]]; then
    echo "::error::$tag isn't a release version, such as v0.2.0 or v0.2.0-rc.1"
    exit 1
  fi
  if ! git merge-base --is-ancestor "$GITHUB_SHA" origin/main; then
    echo "::error::$tag isn't on main"
    exit 1
  fi
  version=${tag#v}
  newest=$(newest_release origin/main)
  if [[ $version == *-* ]]; then
    # A pre-release comes before its release, so it must be for a version after the newest release
    release=v${version%%-*}
    if [ -n "$newest" ] && [ "$(printf '%s\n' "$newest" "$release" | sort -V | tail -n 1)" = "$newest" ]; then
      echo "::error::$tag is for $release, which isn't after $newest, the newest release"
      exit 1
    fi
    # npm publishes a pre-release only under a dist-tag other than latest
    npm_tag=next
  else
    # It's on main, so the newest release is it, or a later one
    if [ "$tag" != "$newest" ]; then
      echo "::error::$tag is older than $newest, the newest release, and would move latest back"
      exit 1
    fi
    npm_tag=latest
  fi
else
  release=$(newest_release "$GITHUB_SHA")
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
