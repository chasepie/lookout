export type ScheduleKind = 'interval' | 'daily' | 'cron';
export type IntervalUnit = 'minutes' | 'hours' | 'days';

export interface Schedule {
  kind: ScheduleKind;
  every: number | null;
  unit: IntervalUnit | null;
  timeOfDay: string | null;
  cron: string | null;
}
