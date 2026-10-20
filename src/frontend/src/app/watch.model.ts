export type ScheduleKind = 'interval' | 'daily' | 'cron';
export type IntervalUnit = 'minutes' | 'hours' | 'days';

export interface Schedule {
  kind: ScheduleKind;
  every: number | null;
  unit: IntervalUnit | null;
  timeOfDay: string | null;
  cron: string | null;
}

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
  baseline: string | null;
  lastRun: Run | null;
  schedule: Schedule;
  jitterMinutes: number;
  nextRunAt: string | null;
}
