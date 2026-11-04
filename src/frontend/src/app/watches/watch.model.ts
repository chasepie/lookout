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
}

export interface Watch extends CreateWatchDto {
  id: number;
  scriptFileName: string | null;
  scriptFileId: string | null;
  watchRuns: WatchRun[];
}

export interface WatchRun {
  id: number;
  watchId: number;
  createdTime: string;
  startTime: string | null;
  endTime: string | null;
  status: WatchRunStatus;
  content: string | null;
  error: string | null;
  /** Base64-encoded image bytes. */
  screenshot: string | null;
}

/** The result of deleting all of a watch's runs. Mirrors `DeleteRunsResult` in src/Backend/Controllers/WatchRunsController.cs. */
export interface DeleteRunsResult {
  deleted: number;
  /** Runs kept because they were in progress. */
  keptRunning: number;
}

/** A run's state as pushed by `LiveUpdates`. Mirrors `WatchRunUpdate` in src/Backend/Hubs/UpdatesHub.cs. */
export type WatchRunUpdate = Pick<WatchRun, 'id' | 'watchId' | 'status' | 'createdTime' | 'startTime' | 'endTime'>;
