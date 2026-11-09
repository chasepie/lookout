import { WatchRunStatus, WatchRunSummary } from './watch.model';

export type Run = Pick<
  WatchRunSummary,
  'status' | 'content' | 'failureReason' | 'error' | 'createdTime' | 'startTime' | 'endTime'
>;

/**
 * What became of a run: still `queued` or `running`, or finished having `crashed`, returned a `knownFailure`,
 * `observed` some content, or finished `empty`, with nothing to report.
 */
export type RunOutcome = 'queued' | 'running' | 'crashed' | 'knownFailure' | 'observed' | 'empty';

export function outcomeOf(run: Run): RunOutcome {
  switch (run.status) {
    case WatchRunStatus.Created:
      return 'queued';
    case WatchRunStatus.Running:
      return 'running';
    default:
      return run.error ? 'crashed' : run.failureReason ? 'knownFailure' : run.content ? 'observed' : 'empty';
  }
}

/** Whether a run ended without a success: it crashed, or the script returned a known failure. */
export function isFailure(outcome: RunOutcome): boolean {
  return outcome === 'crashed' || outcome === 'knownFailure';
}

/** What a run that has nothing to observe yet says instead, or the label before why it failed. */
export const outcomeLabels: Record<Exclude<RunOutcome, 'observed'>, string> = {
  queued: 'Waiting to start',
  running: 'Running',
  crashed: 'Crashed',
  knownFailure: 'Known failure',
  empty: 'Finished with nothing to report',
};

/**
 * Whether each run saw the same thing as the run before it that saw anything, given runs newest first.
 * Failures and unfinished runs are skipped over, so a failure between two identical results isn't a change.
 * The oldest observation has nothing to compare with, so it doesn't count as a repeat.
 */
export function repeats(newestFirst: readonly Run[]): boolean[] {
  const result = new Array<boolean>(newestFirst.length).fill(false);
  let newerContent: string | null = null;
  let newerIndex = -1;
  for (let i = 0; i < newestFirst.length; i++) {
    const run = newestFirst[i];
    if (outcomeOf(run) !== 'observed') {
      continue;
    }
    if (newerIndex >= 0) {
      result[newerIndex] = newerContent === run.content;
    }
    newerContent = run.content;
    newerIndex = i;
  }
  return result;
}

/** How long a run waited to start and how long it took, in milliseconds, as far as it has got. */
export function timingsOf(run: Run): { waited: number | null; ran: number | null } {
  const created = Date.parse(run.createdTime);
  const started = run.startTime ? Date.parse(run.startTime) : null;
  const ended = run.endTime ? Date.parse(run.endTime) : null;
  return {
    waited: started === null ? null : started - created,
    ran: started === null || ended === null ? null : ended - started,
  };
}

/** The newest run that finished, which is what a watch last saw. */
export function latestFinished<T extends Run>(newestFirst: readonly T[]): T | undefined {
  return newestFirst.find((run) => run.status === WatchRunStatus.Done);
}

/**
 * Whether each run saw the same as the newer run listed above it, given runs newest first: a logbook's ditto
 * mark. As with {@link repeats}, failures and unfinished runs are skipped over.
 */
export function sameAsAbove(newestFirst: readonly Run[]): boolean[] {
  return repeats([...newestFirst].reverse()).reverse();
}
