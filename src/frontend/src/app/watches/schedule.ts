import { CreateWatchDto, WatchScheduleKind } from './watch.model';

type Schedule = Pick<CreateWatchDto, 'scheduleEnabled' | 'scheduleKind' | 'scheduleInterval'>;

/** Singular and plural units for each interval kind. */
export const intervalUnits: Record<Exclude<WatchScheduleKind, WatchScheduleKind.Cron>, [string, string]> = {
  [WatchScheduleKind.Minutely]: ['minute', 'minutes'],
  [WatchScheduleKind.Hourly]: ['hour', 'hours'],
  [WatchScheduleKind.Daily]: ['day', 'days'],
  [WatchScheduleKind.Weekly]: ['week', 'weeks'],
};

/** A watch's schedule in plain words: "Every 15 minutes", "Every day", "Cron 0 9 * * 1-5", "Not scheduled". */
export function describeSchedule(watch: Schedule): string {
  if (!watch.scheduleEnabled || watch.scheduleKind === null || !watch.scheduleInterval) {
    return 'Not scheduled';
  }
  if (watch.scheduleKind === WatchScheduleKind.Cron) {
    return `Cron ${watch.scheduleInterval}`;
  }
  const count = Number(watch.scheduleInterval);
  const [one, many] = intervalUnits[watch.scheduleKind];
  return count === 1 ? `Every ${one}` : `Every ${count} ${many}`;
}
