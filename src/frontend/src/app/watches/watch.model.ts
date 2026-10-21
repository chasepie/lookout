import type { Schedule } from '../schedules/schedule.model';

export type RunOutcome = 'succeeded' | 'knownFailure' | 'errored';
export type WatchStatus = 'pending' | 'healthy' | 'failing' | 'paused';

export const outcomeLabels: Record<RunOutcome, string> = {
  succeeded: 'Succeeded', knownFailure: 'Known failure', errored: 'Errored',
};

export const statusLabels: Record<WatchStatus, string> = {
  pending: 'Pending', healthy: 'Healthy', failing: 'Failing', paused: 'Paused',
};

export interface Run {
  id: number;
  outcome: RunOutcome;
  content: string | null;
  details: string | null;
  errorMessage: string | null;
  durationMs: number;
  startedAt: string;
  finishedAt: string;
  isChange: boolean;
}

export interface Watch {
  id: string;
  name: string;
  notificationUrl: string;
  baseline: string | null;
  status: WatchStatus;
  isPaused: boolean;
  failureStreak: number;
  timeoutSeconds: number;
  lastRun: Run | null;
  schedule: Schedule;
  jitterMinutes: number;
  nextRunAt: string | null;
  channelIds: string[];
}

export interface WatchSettings {
  name: string;
  notificationUrl: string;
  timeoutSeconds: number;
}
