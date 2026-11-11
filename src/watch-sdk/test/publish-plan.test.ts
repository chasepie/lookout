import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { compareVersions, parsePackument, planPublish, sdkVersion, supersededTag, type RegistryState } from '../scripts/publish-plan.ts';

const sha = '280d26a6e1734fdbca96040b94ab16f152b3905c';
const empty: RegistryState = { distTags: {}, versions: [] };

function plan(ref: string, registry: RegistryState, commitTime = '20261110143000') {
  return planPublish({ ref, headSha: sha, commitTime, registry });
}

describe('sdkVersion', () => {
  test('main builds sort by commit time and name the commit', () => {
    assert.equal(sdkVersion('main', sha, '20261110143000'), '0.0.0-main.20261110143000.sha-280d26a');
  });

  test('a release tag is its version', () => {
    assert.equal(sdkVersion('v1.4.0', sha, ''), '1.4.0');
    assert.equal(sdkVersion('v1.3.0-rc.1', sha, ''), '1.3.0-rc.1');
  });

  for (const ref of ['vfoo', '1.2.3', 'v1.2', 'v01.2.3', 'v1.2.3-01', 'v1.2.3-rc..1', 'v1.2.3+build']) {
    test(`refuses ${ref}`, () => {
      assert.throws(() => sdkVersion(ref, sha, ''), /isn't a version tag/);
    });
  }

  test('refuses a main build without a commit time', () => {
    assert.throws(() => sdkVersion('main', sha, ''), /isn't a commit time/);
  });
});

describe('compareVersions', () => {
  const ascending = [
    '0.0.0-main.20261110143000.sha-280d26a',
    '0.0.0-main.20261110153000.sha-0123456',
    '0.0.0-main.20261111000000.sha-abcdef0',
    '1.0.0-alpha',
    '1.0.0-alpha.1',
    '1.0.0-alpha.beta',
    '1.0.0-beta',
    '1.0.0-beta.2',
    '1.0.0-beta.11',
    '1.0.0-rc.1',
    '1.0.0',
    '1.2.0',
    '1.10.0',
    '2.0.0',
  ];

  for (let i = 1; i < ascending.length; i++) {
    const [lower = '', higher = ''] = [ascending[i - 1], ascending[i]];
    test(`${lower} < ${higher}`, () => {
      assert.ok(compareVersions(lower, higher) < 0);
      assert.ok(compareVersions(higher, lower) > 0);
    });
  }

  test('equal versions', () => {
    assert.equal(compareVersions('1.2.3-rc.1', '1.2.3-rc.1'), 0);
  });
});

describe('planPublish', () => {
  test('the first main build goes under main', () => {
    assert.deepEqual(plan('main', empty), { publish: true, version: '0.0.0-main.20261110143000.sha-280d26a', tag: 'main' });
  });

  test('a later main build moves main', () => {
    const registry = { distTags: { main: '0.0.0-main.20261109000000.sha-1111111' }, versions: ['0.0.0-main.20261109000000.sha-1111111'] };
    assert.deepEqual(plan('main', registry), { publish: true, version: '0.0.0-main.20261110143000.sha-280d26a', tag: 'main' });
  });

  test('an older main build, such as one whose CD finished late, leaves main where it is', () => {
    const registry = { distTags: { main: '0.0.0-main.20261111000000.sha-2222222' }, versions: ['0.0.0-main.20261111000000.sha-2222222'] };
    assert.deepEqual(plan('main', registry), { publish: true, version: '0.0.0-main.20261110143000.sha-280d26a', tag: supersededTag });
  });

  test('a re-run publishes nothing', () => {
    const registry = { distTags: { main: '0.0.0-main.20261110143000.sha-280d26a' }, versions: ['0.0.0-main.20261110143000.sha-280d26a'] };
    assert.deepEqual(plan('main', registry), {
      publish: false,
      version: '0.0.0-main.20261110143000.sha-280d26a',
      reason: '0.0.0-main.20261110143000.sha-280d26a is already published.',
    });
  });

  test('the highest release moves latest', () => {
    assert.deepEqual(plan('v1.4.0', { distTags: { latest: '1.3.1' }, versions: ['1.3.1'] }), { publish: true, version: '1.4.0', tag: 'latest' });
  });

  test('a release older than latest, such as a backport, leaves latest where it is', () => {
    assert.deepEqual(plan('v1.3.2', { distTags: { latest: '1.4.0' }, versions: ['1.4.0'] }), { publish: true, version: '1.3.2', tag: supersededTag });
  });

  test('the first release moves latest from a main build the registry gave it', () => {
    const registry = { distTags: { latest: '0.0.0-main.20261110143000.sha-280d26a' }, versions: ['0.0.0-main.20261110143000.sha-280d26a'] };
    assert.deepEqual(plan('v1.0.0', registry), { publish: true, version: '1.0.0', tag: 'latest' });
  });

  test('a pre-release ahead of next and latest moves next', () => {
    assert.deepEqual(plan('v1.4.0-rc.2', { distTags: { latest: '1.3.1', next: '1.4.0-rc.1' }, versions: [] }), { publish: true, version: '1.4.0-rc.2', tag: 'next' });
  });

  test('a pre-release behind next leaves next where it is', () => {
    assert.deepEqual(plan('v1.3.1-rc.1', { distTags: { latest: '1.3.0', next: '1.4.0-rc.2' }, versions: [] }), { publish: true, version: '1.3.1-rc.1', tag: supersededTag });
  });

  test('a pre-release of a version already released leaves next where it is', () => {
    assert.deepEqual(plan('v1.4.0-rc.3', { distTags: { latest: '1.4.0', next: '1.4.0-rc.2' }, versions: [] }), { publish: true, version: '1.4.0-rc.3', tag: supersededTag });
  });
});

describe('parsePackument', () => {
  test('a package not published yet', () => {
    assert.deepEqual(parsePackument(''), empty);
  });

  test('every version and dist-tag, with or without latest', () => {
    const json = JSON.stringify({
      name: '@chasepie/lookout-watch-sdk',
      'dist-tags': { main: '0.0.0-main.20261110143000.sha-280d26a' },
      versions: { '0.0.0-main.20261109000000.sha-1111111': {}, '0.0.0-main.20261110143000.sha-280d26a': {} },
    });
    assert.deepEqual(parsePackument(json), {
      distTags: { main: '0.0.0-main.20261110143000.sha-280d26a' },
      versions: ['0.0.0-main.20261109000000.sha-1111111', '0.0.0-main.20261110143000.sha-280d26a'],
    });
  });

  test('refuses dist-tags that aren\'t versions', () => {
    assert.throws(() => parsePackument(JSON.stringify({ 'dist-tags': { main: 1 }, versions: {} })), /dist-tags/);
  });
});
