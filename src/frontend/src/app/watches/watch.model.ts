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
  scriptVersionId: string;
  outcome: RunOutcome;
  content: string | null;
  details: string | null;
  errorMessage: string | null;
  durationMs: number;
  startedAt: string;
  finishedAt: string;
  isChange: boolean;
}

/** How many of a Watch's most recent Known failure and Errored Runs keep their debug output */
export const debugOutputKeptPerWatch = 20;

/** What a Known failure or Errored Run left to debug it by; its screenshot is served separately */
export interface DebugOutput {
  consoleOutput: string | null;
  hasScreenshot: boolean;
}

export interface Watch {
  id: string;
  name: string;
  notificationUrl: string;
  baseline: string | null;
  status: WatchStatus;
  /** Whether a Run is in progress, shown alongside the status. */
  isRunning: boolean;
  isPaused: boolean;
  failureStreak: number;
  timeoutSeconds: number;
  lastRun: Run | null;
  schedule: Schedule;
  jitterMinutes: number;
  nextRunAt: string | null;
  channelIds: string[];
}

export interface ScriptVersion {
  id: string;
  /** Counts the Watch's Script versions from 1, in the order they were uploaded. */
  number: number;
  fileKind: 'ts' | 'js';
  uploadedAt: string;
  isCurrent: boolean;
}

export interface ScriptVersionSource extends ScriptVersion {
  source: string;
}

/** The result of a Test run, which is never recorded, so it has no ID or timestamps. */
export interface TestRun {
  outcome: RunOutcome;
  content: string | null;
  details: string | null;
  errorMessage: string | null;
  durationMs: number;
  /** A base64 PNG of the Script's page, if it got as far as having one. */
  screenshot: string | null;
  consoleOutput: string | null;
}

export interface WatchSettings {
  name: string;
  notificationUrl: string;
  timeoutSeconds: number;
}
