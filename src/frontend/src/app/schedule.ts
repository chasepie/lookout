import { hidden, max, min, required, schema, validate } from '@angular/forms/signals';
import type { IntervalUnit, Schedule, ScheduleKind } from './watch.model';

/** The schedule picker's fields; only those its kind uses are sent. */
export interface ScheduleForm {
  kind: ScheduleKind;
  every: number;
  unit: IntervalUnit;
  timeOfDay: string;
  cron: string;
  jitterMinutes: number;
}

export const defaultSchedule = (): ScheduleForm => ({
  kind: 'interval', every: 1, unit: 'hours', timeOfDay: '09:00', cron: '', jitterMinutes: 0,
});

/** The picker's fields for a Watch's saved schedule, with defaults for the fields its kind doesn't use. */
export function scheduleFormFor(schedule: Schedule, jitterMinutes: number): ScheduleForm {
  const defaults = defaultSchedule();
  return {
    kind: schedule.kind,
    every: schedule.every ?? defaults.every,
    unit: schedule.unit ?? defaults.unit,
    // The API sends hh:mm:ss; a time input shows hh:mm.
    timeOfDay: schedule.timeOfDay?.slice(0, 5) ?? defaults.timeOfDay,
    cron: schedule.cron ?? defaults.cron,
    jitterMinutes,
  };
}

const minutesPer: Record<IntervalUnit, number> = { minutes: 1, hours: 60, days: 1440 };

/** Mirrors the backend's rules, which also check cron expressions against the 3-minute minimum. */
export const scheduleSchema = schema<ScheduleForm>((path) => {
  hidden(path.every, (field) => field.valueOf(path.kind) !== 'interval');
  hidden(path.unit, (field) => field.valueOf(path.kind) !== 'interval');
  hidden(path.timeOfDay, (field) => field.valueOf(path.kind) !== 'daily');
  hidden(path.cron, (field) => field.valueOf(path.kind) !== 'cron');
  required(path.every);
  validate(path.every, (field) => {
    const every = field.value();
    const minutes = every * minutesPer[field.valueOf(path.unit)];
    if (!Number.isInteger(every) || every < 1 || minutes < 3) {
      return { kind: 'minimumInterval', message: 'Schedules must not run more than once every 3 minutes.' };
    }
    return minutes > 365 * 1440
      ? { kind: 'maximumInterval', message: 'An interval schedule must run at least once every 365 days.' }
      : undefined;
  });
  required(path.timeOfDay, { message: 'Choose a time of day.' });
  required(path.cron, { message: 'Enter a cron expression.' });
  min(path.jitterMinutes, 0, { message: 'Jitter must be between 0 and 60 minutes.' });
  max(path.jitterMinutes, 60, { message: 'Jitter must be between 0 and 60 minutes.' });
  // min and max pass an empty or fractional number, which the backend can't read as whole minutes.
  validate(path.jitterMinutes, (field) => Number.isInteger(field.value())
    ? undefined
    : { kind: 'wholeMinutes', message: 'Jitter must be between 0 and 60 minutes.' });
});

/** The fields the backend reads for this schedule, for a form upload or a JSON body. */
export function scheduleFields(schedule: ScheduleForm): Record<string, string | number> {
  const { kind, jitterMinutes } = schedule;
  switch (kind) {
    case 'interval': return { scheduleKind: kind, jitterMinutes, every: schedule.every, unit: schedule.unit };
    case 'daily': return { scheduleKind: kind, jitterMinutes, timeOfDay: schedule.timeOfDay };
    case 'cron': return { scheduleKind: kind, jitterMinutes, cron: schedule.cron };
  }
}
