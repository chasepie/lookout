export type RunOutcome = 'succeeded' | 'knownFailure' | 'errored';

export interface Run {
  id: number;
  outcome: RunOutcome;
  content: string | null;
  details: string | null;
  errorMessage: string | null;
  durationMs: number;
  finishedAt: string;
  isChange: boolean;
}

export interface Watch {
  id: string;
  name: string;
  notificationUrl: string;
  latestContent: string | null;
  lastRun: Run | null;
}
