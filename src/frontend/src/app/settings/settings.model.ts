/** The install-wide settings at /api/settings */
export interface LookoutSettings {
  /** How many days a Run is kept before it is pruned, unless it is a Change */
  retentionDays: number;
  /** The IANA time zone daily and cron schedules use, such as Europe/London */
  timeZone: string;
}
