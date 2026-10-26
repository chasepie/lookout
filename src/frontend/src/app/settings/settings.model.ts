/** The install-wide settings at /api/settings */
export interface LookoutSettings {
  /** How many days a Run is kept before it is pruned, unless it is a Change */
  retentionDays: number;
  /** The install's saved IANA time zone, such as Europe/London */
  timeZone: string;
  /**
   * The time zone daily and cron Schedules use: the saved one, or the one Lookout falls back to while its host doesn't
   * know the saved one
   */
  effectiveTimeZone: string;
}
