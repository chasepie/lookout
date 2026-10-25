import type { ChildProcess } from 'node:child_process';
import { PassThrough } from 'node:stream';
import { finished } from 'node:stream/promises';
import { expect, test } from 'vitest';
import { collectConsoleOutput } from './console-output.ts';

const cutOutputMarker = '[Earlier output was cut]\n';
const maxConsoleOutputLength = 256 * 1024;
const stderrTailLength = 16 * 1024;

/** A process's stdout and stderr, whose output is collected as a Script's is */
function fakeProcess() {
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const collected = collectConsoleOutput({ stdout, stderr } as unknown as ChildProcess);
  return {
    stdout,
    stderr,
    /** Ends both streams, resolving with what was collected once all of it has arrived */
    end: async () => {
      stdout.end();
      stderr.end();
      await Promise.all([finished(stdout), finished(stderr)]);
      return { consoleOutput: collected.consoleOutput(), stderr: collected.stderr() };
    },
  };
}

test('a process that writes nothing has empty console output', async () => {
  const child = fakeProcess();

  expect(await child.end()).toEqual({ consoleOutput: '', stderr: '' });
});

test('stdout and stderr both go into the console output, and only stderr into its tail', async () => {
  const child = fakeProcess();

  child.stdout.write('to stdout\n');
  child.stderr.write('to stderr\n');

  const { consoleOutput, stderr } = await child.end();
  expect(consoleOutput).toContain('to stdout\n');
  expect(consoleOutput).toContain('to stderr\n');
  expect(stderr).toBe('to stderr\n');
});

test('output up to the limit is kept whole, without a mark', async () => {
  const child = fakeProcess();
  const output = 'x'.repeat(maxConsoleOutputLength);

  child.stdout.write(output);

  expect((await child.end()).consoleOutput).toBe(output);
});

test('output just over the limit is cut down to its last 262,144 characters, marking the cut', async () => {
  const child = fakeProcess();

  child.stdout.write('x'.repeat(maxConsoleOutputLength));
  child.stdout.write('END\n');

  const { consoleOutput } = await child.end();
  expect(consoleOutput).toBe(`${cutOutputMarker}${'x'.repeat(maxConsoleOutputLength - 4)}END\n`);
});

test('output many times the limit, over many writes, is cut down to its last 262,144 characters', async () => {
  const child = fakeProcess();

  for (let line = 1; line <= 20_000; line++) {
    child.stdout.write(`line ${line} ${'x'.repeat(50)}\n`);
  }

  const { consoleOutput } = await child.end();
  expect(consoleOutput.startsWith(cutOutputMarker)).toBe(true);
  expect(consoleOutput.length).toBe(cutOutputMarker.length + maxConsoleOutputLength);
  expect(consoleOutput.endsWith(`line 20000 ${'x'.repeat(50)}\n`)).toBe(true);
});

test('output trimmed down to the limit by its last write is still marked as cut', async () => {
  const child = fakeProcess();

  child.stdout.write('x'.repeat(maxConsoleOutputLength));
  // Over twice the limit in all, so the runner trims it as it arrives, leaving exactly the limit
  child.stdout.write(`${'y'.repeat(maxConsoleOutputLength)}END\n`);

  const { consoleOutput } = await child.end();
  expect(consoleOutput).toBe(`${cutOutputMarker}${'y'.repeat(maxConsoleOutputLength - 4)}END\n`);
});

test("output cut in the middle of a character doesn't keep half of it", async () => {
  const child = fakeProcess();

  // An odd number of UTF-16 code units, each emoji taking two, so the cut goes through one
  child.stdout.write(`${'😀'.repeat(150_000)}\n`);

  const { consoleOutput } = await child.end();
  expect(consoleOutput.startsWith(`${cutOutputMarker}😀`)).toBe(true);
  expect(consoleOutput.isWellFormed()).toBe(true);
});

test('a character split between writes is kept whole', async () => {
  const child = fakeProcess();
  const bytes = Buffer.from('😀');

  child.stdout.write(bytes.subarray(0, 2));
  child.stdout.write(bytes.subarray(2));

  expect((await child.end()).consoleOutput).toBe('😀');
});

test("stderr's tail is cut down to its last 16,384 characters, however much stdout there is", async () => {
  const child = fakeProcess();

  child.stdout.write('x'.repeat(maxConsoleOutputLength));
  child.stderr.write('e'.repeat(stderrTailLength));
  child.stderr.write('END\n');

  const { stderr } = await child.end();
  expect(stderr).toBe(`${cutOutputMarker}${'e'.repeat(stderrTailLength - 4)}END\n`);
});
