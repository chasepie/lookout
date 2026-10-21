import { type SchemaPathTree, max, maxLength, min, pattern, required, validate } from '@angular/forms/signals';

/** The backend's timeout limits for a Watch's Runs */
export const defaultTimeoutSeconds = 120;
export const maxTimeoutSeconds = 600;

/** The Name and Notification URL rules the backend applies once it has trimmed both */
export function nameAndNotificationUrlRules(fields: SchemaPathTree<{ name: string; notificationUrl: string }>) {
  required(fields.name);
  pattern(fields.name, /\S/, { message: 'Enter a name.' });
  maxLength(fields.name, 200);
  required(fields.notificationUrl);
  pattern(fields.notificationUrl, /^\s*https?:\/\/\S+\s*$/i, { message: 'Enter an HTTP or HTTPS URL.' });
  maxLength(fields.notificationUrl, 2048);
}

export function timeoutRules(timeoutSeconds: SchemaPathTree<number>) {
  const message = `Enter a whole number of seconds between 1 and ${maxTimeoutSeconds}.`;
  required(timeoutSeconds, { message });
  min(timeoutSeconds, 1, { message });
  max(timeoutSeconds, maxTimeoutSeconds, { message });
  // min and max pass a fractional number, which the backend can't read as whole seconds.
  validate(timeoutSeconds, (field) => Number.isInteger(field.value())
    ? undefined
    : { kind: 'wholeSeconds', message });
}
