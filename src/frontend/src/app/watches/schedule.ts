import cronstrue from 'cronstrue';
import { CreateWatchDto, WatchScheduleKind } from './watch.model';

type Schedule = Pick<CreateWatchDto, 'scheduleEnabled' | 'scheduleKind' | 'scheduleInterval'>;

/** Singular and plural units for each interval kind. */
export const intervalUnits: Record<Exclude<WatchScheduleKind, WatchScheduleKind.Cron>, [string, string]> = {
  [WatchScheduleKind.Minutely]: ['minute', 'minutes'],
  [WatchScheduleKind.Hourly]: ['hour', 'hours'],
  [WatchScheduleKind.Daily]: ['day', 'days'],
  [WatchScheduleKind.Weekly]: ['week', 'weeks'],
};

/** Whether the Owner's locale writes times on a 24-hour clock. */
const uses24HourTime = ['h23', 'h24'].includes(
  new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hourCycle ?? '',
);

/**
 * A cron expression in plain words, such as "At 09:00 AM, Monday through Friday", or null if it can't be read. Only
 * five-field expressions count, as the Backend accepts no others.
 */
export function describeCron(expression: string, use24HourTimeFormat = uses24HourTime): string | null {
  const trimmed = expression.trim();
  if (trimmed.split(/\s+/).length !== 5) {
    return null;
  }
  try {
    return cronstrue.toString(trimmed, { use24HourTimeFormat });
  } catch {
    return null;
  }
}

/**
 * A watch's schedule in plain words: "Every 15 minutes", "Every day", "At 09:00 AM, Monday through Friday", "Not
 * scheduled". A cron expression that can't be put into words is shown as it is: "Cron 0 9 * * 1-5".
 */
export function describeSchedule(watch: Schedule): string {
  if (!watch.scheduleEnabled || watch.scheduleKind === null || !watch.scheduleInterval) {
    return 'Not scheduled';
  }
  if (watch.scheduleKind === WatchScheduleKind.Cron) {
    return describeCron(watch.scheduleInterval) ?? `Cron ${watch.scheduleInterval}`;
  }
  const count = Number(watch.scheduleInterval);
  const [one, many] = intervalUnits[watch.scheduleKind];
  return count === 1 ? `Every ${one}` : `Every ${count} ${many}`;
}
