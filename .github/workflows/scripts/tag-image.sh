#!/usr/bin/env bash
# Joins an image's builds, one per platform, into one image for both platforms, and tags it, for CD (see "Releasing" in
# README.md). The builds are those Build images pushed by digest, each recorded as an empty file named after its digest
# in $RUNNER_TEMP/digests/digest-<image>-<arch>. The tags, and the annotations GHCR shows, come from metadata-action:
#   tag-image.sh <image> <metadata-action's JSON output>
set -euo pipefail

image=$1
metadata=$2
repository="ghcr.io/$GITHUB_REPOSITORY_OWNER/$image"

# Assigned first, so a failure stops the script rather than leaving no tags
options=$(jq --raw-output '(.tags[] | "--tag", .), (.annotations[] | "--annotation", .)' <<< "$metadata")
mapfile -t options <<< "$options"

# The platforms Build images builds. Listed rather than matched, as digest-lookout-* would match lookout-runner's too.
sources=()
for arch in amd64 arm64; do
  for digest in "$RUNNER_TEMP/digests/digest-$image-$arch"/*; do
    sources+=("$repository@sha256:$(basename "$digest")")
  done
done

docker buildx imagetools create "${options[@]}" "${sources[@]}"
docker buildx imagetools inspect "$(jq --raw-output '.tags[0]' <<< "$metadata")"
