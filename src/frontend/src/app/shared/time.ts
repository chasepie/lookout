const minute = 60_000;
const hour = 60 * minute;
const day = 24 * hour;

/** Absolute times follow the browser's locale, so they read the way the Owner is used to. */
const timeOfDay = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const timeOfDayWithSeconds = new Intl.DateTimeFormat(undefined, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});
const dayInYear = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long' });
const dayWithYear = new Intl.DateTimeFormat(undefined, {
  weekday: 'long',
  day: 'numeric',
  month: 'long',
  year: 'numeric',
});
const shortDay = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });
const full = new Intl.DateTimeFormat(undefined, { dateStyle: 'full', timeStyle: 'medium' });
const brief = new Intl.DateTimeFormat(undefined, {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
});
const briefWithYear = new Intl.DateTimeFormat(undefined, {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

/** The start of the local day `time` falls in. */
function startOfDay(time: number) {
  const date = new Date(time);
  date.setHours(0, 0, 0, 0);
  return date.getTime();
}

/**
 * How long ago (or how soon) `time` is, in a few words: "just now", "4 min ago", "in 2 h", "yesterday",
 * or a date when it's further off.
 */
export function relativeTime(time: number, now: number): string {
  const delta = time - now;
  const distance = Math.abs(delta);
  const future = delta > 0;
  if (distance < 45_000) {
    return future ? 'in a moment' : 'just now';
  }
  if (distance < hour) {
    const minutes = Math.max(1, Math.round(distance / minute));
    return future ? `in ${minutes} min` : `${minutes} min ago`;
  }
  if (distance < 12 * hour) {
    const hours = Math.round(distance / hour);
    return future ? `in ${hours} h` : `${hours} h ago`;
  }
  const days = Math.round((startOfDay(time) - startOfDay(now)) / day);
  if (days === 0) {
    return future ? `later today, ${timeOfDay.format(time)}` : `today, ${timeOfDay.format(time)}`;
  }
  if (days === -1) {
    return `yesterday, ${timeOfDay.format(time)}`;
  }
  if (days === 1) {
    return `tomorrow, ${timeOfDay.format(time)}`;
  }
  return shortDay.format(time);
}

/** The heading for a day of runs: "Today", "Yesterday", or the date, with the year only when it isn't this one. */
export function dayHeading(time: number, now: number): string {
  const days = Math.round((startOfDay(now) - startOfDay(time)) / day);
  if (days === 0) {
    return 'Today';
  }
  if (days === 1) {
    return 'Yesterday';
  }
  const sameYear = new Date(time).getFullYear() === new Date(now).getFullYear();
  return (sameYear ? dayInYear : dayWithYear).format(time);
}

/** A key that's the same for every time in one local day, for grouping. */
export function dayKey(time: number): number {
  return startOfDay(time);
}

/** The time of day, as a log entry shows it. */
export function clockTime(time: number, withSeconds = false): string {
  return (withSeconds ? timeOfDayWithSeconds : timeOfDay).format(time);
}

/** The day and time in few words, such as "Tue 3 Nov, 09:00", for runs that may be days apart. */
export function briefTime(time: number): string {
  return brief.format(time);
}

/** As {@link briefTime}, with the year when it isn't `now`'s, for times that may be years off, such as cron runs. */
export function briefTimeInYear(time: number, now: number): string {
  const sameYear = new Date(time).getFullYear() === new Date(now).getFullYear();
  return (sameYear ? brief : briefWithYear).format(time);
}

/** The full date and time, for tooltips and wherever the exact moment matters. */
export function fullTime(time: number): string {
  return full.format(time);
}

/** A length of time in the largest units that keep it readable: "0.4 s", "12 s", "3 min 5 s", "2 h 10 min". */
export function duration(ms: number): string {
  if (ms < 9_950) {
    return `${(Math.max(0, ms) / 1000).toFixed(1)} s`;
  }
  // Round the whole first, then split it, so a remainder can't round up to a whole unit: never "59 min 60 s"
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) {
    return `${seconds} s`;
  }
  if (seconds < 3600) {
    const minutes = Math.floor(seconds / 60);
    const rest = seconds % 60;
    return rest ? `${minutes} min ${rest} s` : `${minutes} min`;
  }
  const totalMinutes = Math.round(ms / minute);
  const hours = Math.floor(totalMinutes / 60);
  const rest = totalMinutes % 60;
  return rest ? `${hours} h ${rest} min` : `${hours} h`;
}
