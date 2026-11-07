import { dayHeading, dayKey, duration, relativeTime } from './time';

const now = new Date(2026, 10, 7, 15, 0, 0).getTime();
const minutes = (n: number) => n * 60_000;

describe('relativeTime', () => {
  it.each([
    ['just now', now - 10_000],
    ['in a moment', now + 10_000],
    ['4 min ago', now - minutes(4)],
    ['in 12 min', now + minutes(12)],
    ['3 h ago', now - minutes(180)],
    ['in 2 h', now + minutes(120)],
  ])('says %s', (expected, time) => {
    expect(relativeTime(time, now)).toBe(expected);
  });

  it('names yesterday', () => {
    expect(relativeTime(new Date(2026, 10, 6, 9, 30).getTime(), now)).toMatch(/^yesterday, /);
  });

  it('gives a date further back', () => {
    expect(relativeTime(new Date(2026, 9, 20, 9, 30).getTime(), now)).not.toMatch(/ago|yesterday/);
  });
});

describe('dayHeading', () => {
  it('names today and yesterday', () => {
    expect(dayHeading(new Date(2026, 10, 7, 0, 5).getTime(), now)).toBe('Today');
    expect(dayHeading(new Date(2026, 10, 6, 23, 55).getTime(), now)).toBe('Yesterday');
  });

  it('leaves the year out for this year only', () => {
    expect(dayHeading(new Date(2026, 10, 3).getTime(), now)).not.toContain('2026');
    expect(dayHeading(new Date(2025, 10, 3).getTime(), now)).toContain('2025');
  });
});

describe('dayKey', () => {
  it('is the same throughout a day', () => {
    expect(dayKey(new Date(2026, 10, 7, 0, 1).getTime())).toBe(dayKey(new Date(2026, 10, 7, 23, 59).getTime()));
  });
});

describe('duration', () => {
  it.each([
    [400, '0.4 s'],
    [6_240, '6.2 s'],
    [42_000, '42 s'],
    [185_000, '3 min 5 s'],
    [minutes(130), '2 h 10 min'],
    [minutes(60), '1 h'],
  ])('writes %i ms as %s', (ms, expected) => {
    expect(duration(ms)).toBe(expected);
  });
});
