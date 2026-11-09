/** A number of seconds as the Owner reads it, such as "30 s", "1 min" or "1 h 30 min". */
export function describeSeconds(seconds: number): string {
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const rest = seconds % 60;
  const parts = [hours && `${hours} h`, minutes && `${minutes} min`, rest && `${rest} s`].filter(Boolean);
  return parts.length === 0 ? '0 s' : parts.join(' ');
}
