/**
 * What CD (.github/workflows/cd.yml) publishes the watch SDK as: its version, from the push, and the dist-tag it goes
 * under, from what the registry already has. See docs/adr/0007-watch-sdk-published-in-step-with-lookout.md.
 *
 * As a script, it reads REF, HEAD_SHA and COMMIT_TIME from the environment, and the packument from the file
 * PACKUMENT_FILE, if it exists, and writes `publish`, `version` and `tag` to GITHUB_OUTPUT.
 */
import { appendFile, readFile } from 'node:fs/promises';

/** What the registry has of the package. */
/** The dist-tags this publishes under, and any others the package has. */
export interface DistTags {
  main?: string;
  latest?: string;
  next?: string;
  [tag: string]: string | undefined;
}

export interface RegistryState {
  distTags: DistTags;
  versions: readonly string[];
}

export interface PublishInput {
  /** The branch or tag pushed: `main`, or a release tag such as `v1.4.0`. */
  ref: string;
  headSha: string;
  /** The commit's committer date in UTC, as `YYYYMMDDHHmmss`. */
  commitTime: string;
  registry: RegistryState;
}

export type PublishPlan =
  | { publish: true; version: string; tag: string }
  | { publish: false; version: string; reason: string };

/**
 * Where a version goes when it doesn't move the dist-tag it would have, since npm publishes every version under one.
 * Nothing installs from it, so it moving backwards is harmless.
 */
export const supersededTag = 'superseded';

// semver.org's grammar without build metadata, which npm ignores when comparing versions
const versionPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?$/;

/**
 * The version, and the dist-tag it should go under: `main` for main, `latest` for a release, `next` for a pre-release.
 * A version takes its tag only if it's newer than the version there, and a pre-release only if it's also newer than
 * `latest`; otherwise it goes under {@link supersededTag}. So a re-run, or runs finishing out of order, never move a tag
 * backwards.
 */
export function planPublish({ ref, headSha, commitTime, registry }: PublishInput): PublishPlan {
  const version = sdkVersion(ref, headSha, commitTime);
  if (registry.versions.includes(version)) {
    return { publish: false, version, reason: `${version} is already published.` };
  }

  const tag = ref === 'main' ? 'main' : parseVersion(version).prerelease.length > 0 ? 'next' : 'latest';
  const mustBeAhead = tag === 'next' ? [registry.distTags.next, registry.distTags.latest] : [registry.distTags[tag]];
  const ahead = mustBeAhead.every(current => current === undefined || compareVersions(version, current) > 0);
  return { publish: true, version, tag: ahead ? tag : supersededTag };
}

/**
 * main's builds are `0.0.0-main.<commit time>.sha-<commit>`, which sort by commit time and below every release, so a
 * range such as `^1.2.0` never picks one. A release tag `v1.4.0` is `1.4.0`.
 */
export function sdkVersion(ref: string, headSha: string, commitTime: string): string {
  if (ref === 'main') {
    if (!/^[0-9a-f]{7,40}$/.test(headSha)) {
      throw new Error(`${headSha} isn't a commit SHA.`);
    }
    if (!/^[1-9]\d{13}$/.test(commitTime)) {
      throw new Error(`${commitTime} isn't a commit time such as 20261110143000.`);
    }

    return `0.0.0-main.${commitTime}.sha-${headSha.slice(0, 7)}`;
  }

  const version = ref.startsWith('v') ? ref.slice(1) : '';
  if (!versionPattern.test(version)) {
    throw new Error(`${ref} isn't a version tag such as v1.2.3 or v1.3.0-rc.1, so it has no watch SDK version.`);
  }

  return version;
}

interface ParsedVersion {
  release: [string, string, string];
  prerelease: string[];
}

function parseVersion(version: string): ParsedVersion {
  const match = versionPattern.exec(version);
  if (match === null) {
    throw new Error(`${version} isn't a semver version.`);
  }

  const [, major = '', minor = '', patch = '', prerelease] = match;
  return { release: [major, minor, patch], prerelease: prerelease === undefined ? [] : prerelease.split('.') };
}

/** Compares numeric identifiers of any length; the grammar rules out leading zeros. */
function compareNumbers(a: string, b: string): number {
  return a.length - b.length || (a < b ? -1 : a > b ? 1 : 0);
}

/** Below 0 when `a` comes before `b` in semver's precedence, above 0 after, 0 when equal. */
export function compareVersions(a: string, b: string): number {
  const left = parseVersion(a);
  const right = parseVersion(b);
  for (let i = 0; i < 3; i++) {
    const result = compareNumbers(left.release[i] ?? '', right.release[i] ?? '');
    if (result !== 0) {
      return result;
    }
  }

  // A pre-release comes before its release
  if (left.prerelease.length === 0 || right.prerelease.length === 0) {
    return right.prerelease.length - left.prerelease.length;
  }

  for (let i = 0; i < Math.min(left.prerelease.length, right.prerelease.length); i++) {
    const result = compareIdentifiers(left.prerelease[i] ?? '', right.prerelease[i] ?? '');
    if (result !== 0) {
      return result;
    }
  }

  return left.prerelease.length - right.prerelease.length;
}

/** Numeric identifiers compare as numbers and come before alphanumeric ones, which compare in ASCII order. */
function compareIdentifiers(a: string, b: string): number {
  const aNumeric = /^\d+$/.test(a);
  const bNumeric = /^\d+$/.test(b);
  if (aNumeric && bNumeric) {
    return compareNumbers(a, b);
  }
  if (aNumeric !== bNumeric) {
    return aNumeric ? -1 : 1;
  }

  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Reads a packument, the registry's JSON for a package (`GET <registry>/<package>`), or an empty string for a package
 * that isn't published yet. The packument lists every version and dist-tag, unlike `npm view`, which shows nothing for a
 * package without a `latest`.
 */
export function parsePackument(json: string): RegistryState {
  if (json.trim() === '') {
    return { distTags: {}, versions: [] };
  }

  const value: unknown = JSON.parse(json);
  if (typeof value !== 'object' || value === null) {
    throw new Error('The packument isn\'t an object.');
  }

  const distTags = 'dist-tags' in value ? value['dist-tags'] : {};
  const versions = 'versions' in value ? value.versions : {};
  const tagEntries = typeof distTags === 'object' && distTags !== null ? Object.entries(distTags) : undefined;
  if (!tagEntries?.every((entry): entry is [string, string] => typeof entry[1] === 'string')) {
    throw new Error(`The packument's dist-tags aren't versions: ${JSON.stringify(distTags)}`);
  }
  if (typeof versions !== 'object' || versions === null) {
    throw new Error('The packument\'s versions aren\'t an object.');
  }

  return { distTags: Object.fromEntries(tagEntries), versions: Object.keys(versions) };
}

/** The packument file's contents, or an empty string when there's no file: the package isn't published yet. */
async function readPackument(path: string): Promise<string> {
  try {
    return await readFile(path, 'utf8');
  } catch (error) {
    if (error instanceof Error && 'code' in error && error.code === 'ENOENT') {
      return '';
    }
    throw error;
  }
}

if (import.meta.main) {
  const env = (name: string): string => process.env[name] ?? '';
  try {
    const plan = planPublish({
      ref: env('REF'),
      headSha: env('HEAD_SHA'),
      commitTime: env('COMMIT_TIME'),
      registry: parsePackument(await readPackument(env('PACKUMENT_FILE'))),
    });

    const outputs = plan.publish
      ? `publish=true\nversion=${plan.version}\ntag=${plan.tag}\n`
      : `publish=false\nversion=${plan.version}\n`;
    console.log(plan.publish
      ? `Publishing the watch SDK ${plan.version} under ${plan.tag}.`
      : `::notice::Not publishing the watch SDK: ${plan.reason}`);
    const outputFile = env('GITHUB_OUTPUT');
    if (outputFile !== '') {
      await appendFile(outputFile, outputs);
    }
  } catch (error) {
    console.log(`::error::${error instanceof Error ? error.message : String(error)}`);
    process.exitCode = 1;
  }
}
