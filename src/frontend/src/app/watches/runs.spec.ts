import { describeCron, describeSchedule } from './schedule';
import { outcomeOf, repeats, sameAsAbove, timingsOf } from './runs';
import { WatchRunStatus, WatchScheduleKind } from './watch.model';

const at = '2026-11-07T12:00:00Z';
const observed = (content: string) => ({
  status: WatchRunStatus.Done,
  content,
  failureReason: null,
  error: null,
  createdTime: at,
  startTime: null,
  endTime: null,
});
const failed = { ...observed(''), content: null, error: 'Timeout' };
const running = { ...observed(''), content: null, status: WatchRunStatus.Running };

describe('outcomeOf', () => {
  it('tells the outcomes apart', () => {
    expect(outcomeOf({ ...observed(''), content: null, status: WatchRunStatus.Created })).toBe('queued');
    expect(outcomeOf(running)).toBe('running');
    expect(outcomeOf(failed)).toBe('crashed');
    expect(outcomeOf({ ...observed(''), content: null, failureReason: 'Blocked' })).toBe('knownFailure');
    expect(outcomeOf(observed('Normal'))).toBe('observed');
    expect(outcomeOf({ ...observed(''), content: null })).toBe('empty');
  });
});

describe('repeats', () => {
  it('marks a run that saw the same as the one before', () => {
    expect(repeats([observed('B'), observed('A'), observed('A')])).toEqual([false, true, false]);
  });

  it('looks past failures and unfinished runs', () => {
    expect(repeats([running, observed('A'), failed, observed('A')])).toEqual([false, true, false, false]);
  });
});

describe('sameAsAbove', () => {
  it('marks a run that saw the same as the newer one above it', () => {
    expect(sameAsAbove([observed('B'), observed('B'), failed, observed('A')])).toEqual([false, true, false, false]);
  });
});

describe('timingsOf', () => {
  it('splits waiting from running', () => {
    expect(
      timingsOf({
        ...observed('A'),
        createdTime: '2026-11-07T12:00:00.000Z',
        startTime: '2026-11-07T12:00:00.400Z',
        endTime: '2026-11-07T12:00:06.600Z',
      }),
    ).toEqual({ waited: 400, ran: 6200 });
  });

  it("leaves out what hasn't happened yet", () => {
    expect(timingsOf(observed('A'))).toEqual({ waited: null, ran: null });
  });
});

describe('describeSchedule', () => {
  const schedule = (scheduleKind: WatchScheduleKind, scheduleInterval: string) => ({
    scheduleEnabled: true,
    scheduleKind,
    scheduleInterval,
  });

  it.each([
    [schedule(WatchScheduleKind.Minutely, '15'), 'Every 15 minutes'],
    [schedule(WatchScheduleKind.Hourly, '1'), 'Every hour'],
    [schedule(WatchScheduleKind.Weekly, '2'), 'Every 2 weeks'],
    [schedule(WatchScheduleKind.Cron, '*/5 * * * *'), 'Every 5 minutes'],
    [schedule(WatchScheduleKind.Cron, '60 * * * *'), 'Cron 60 * * * *'],
    [{ ...schedule(WatchScheduleKind.Daily, '1'), scheduleEnabled: false }, 'Not scheduled'],
  ])('describes %o as %s', (watch, expected) => {
    expect(describeSchedule(watch)).toBe(expected);
  });
});

describe('describeCron', () => {
  it.each([
    ['0 9 * * 1-5', false, 'At 09:00 AM, Monday through Friday'],
    ['0 9 * * 1-5', true, 'At 09:00, Monday through Friday'],
    ['  */15 9-16 * * *  ', true, 'Every 15 minutes, between 09:00 and 16:59'],
  ])('describes %s (24-hour: %s) as %s', (expression, use24Hour, expected) => {
    expect(describeCron(expression, use24Hour)).toBe(expected);
  });

  it.each(['', '0 9 * *', '0 0 9 * * 1-5', '60 * * * *', 'not a cron expression'])("can't read %o", (expression) => {
    expect(describeCron(expression)).toBeNull();
  });
});
