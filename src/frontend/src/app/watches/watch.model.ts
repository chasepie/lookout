/** Mirrors `WatchScheduleKind` in src/Core/Watch.cs. The backend serializes enums as numbers. */
export enum WatchScheduleKind {
  Minutely = 0,
  Hourly = 1,
  Daily = 2,
  Weekly = 3,
  Cron = 4,
}

/** Mirrors `WatchRunStatus` in src/Core/WatchRun.cs. */
export enum WatchRunStatus {
  Created = 0,
  Running = 1,
  Done = 2,
}

export interface CreateWatchDto {
  name: string;
  scheduleEnabled: boolean;
  scheduleKind: WatchScheduleKind | null;
  scheduleInterval: string | null;
  /** Whether runs send the browser's traffic through the VPN. */
  useVpn: boolean;
  /** Whether a run that ends in a success gets a full-page screenshot, when the script returned none of its own. */
  screenshotOnSuccess: boolean;
  /** Whether a run that ends in a known failure gets a full-page screenshot, when the script returned none of its own. */
  screenshotOnKnownFailure: boolean;
  /** Whether a run whose script throws gets a full-page screenshot. */
  screenshotOnCrash: boolean;
  /** How many known failures in a row make a known-failure streak worth a notification. */
  knownFailureThreshold: number;
}

/** A watch as creating or editing one returns it. Mirrors `Watch` in src/Core/Watch.cs. */
export interface Watch extends CreateWatchDto {
  id: number;
  scriptFileName: string | null;
  scriptFileId: string | null;
}

/** A run without its screenshot. Mirrors `WatchRunSummary` in src/Backend/Controllers/WatchSummaries.cs. */
export interface WatchRunSummary {
  id: number;
  watchId: number;
  createdTime: string;
  startTime: string | null;
  endTime: string | null;
  status: WatchRunStatus;
  /** What the script found, when it returned a success. */
  content: string | null;
  /** The script's reason, when it returned a known failure. */
  failureReason: string | null;
  /** Why the run crashed. */
  error: string | null;
  hasScreenshot: boolean;
}

/** A single run, with its screenshot. Mirrors `WatchRun` in src/Core/WatchRun.cs. */
export interface WatchRun extends Omit<WatchRunSummary, 'hasScreenshot'> {
  /** Base64-encoded image bytes, of the type in `screenshotMediaType`. */
  screenshot: string | null;
  screenshotMediaType: 'image/png' | 'image/jpeg' | null;
}

/** A watch with its latest runs. Mirrors `WatchSummary` in src/Backend/Controllers/WatchSummaries.cs. */
export interface WatchSummary extends CreateWatchDto {
  id: number;
  scriptFileName: string | null;
  /** At most 24, newest first. */
  recentRuns: WatchRunSummary[];
  /** When the schedule next creates a run; `null` if it isn't scheduled. In the past means any moment now. */
  nextRunTime: string | null;
}

/** When a cron expression would run a watch. Mirrors `CronPreview` in src/Backend/Controllers/CronController.cs. */
export interface CronPreview {
  /** Whether a watch can be saved with the expression. */
  isValid: boolean;
  /** The next few runs, in UTC; none if the expression is invalid or never runs again. */
  nextRuns: string[];
  /** The time zone the expression's times are in: the server's, such as "Etc/UTC". */
  timeZone: string;
}

/** The result of deleting all of a watch's runs. Mirrors `DeleteRunsResult` in src/Backend/Controllers/WatchRunsController.cs. */
export interface DeleteRunsResult {
  deleted: number;
  /** Runs kept because they were in progress. */
  keptRunning: number;
}

/** A run's state as pushed by `LiveUpdates`. Mirrors `WatchRunUpdate` in src/Backend/Hubs/UpdatesHub.cs. */
export type WatchRunUpdate = Pick<WatchRun, 'id' | 'watchId' | 'status' | 'createdTime' | 'startTime' | 'endTime'>;
