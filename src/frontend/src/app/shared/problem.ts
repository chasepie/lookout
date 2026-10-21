import { HttpErrorResponse } from '@angular/common/http';

/** The message from a backend problem response, or the fallback when it has none. */
export function problemMessage(error: unknown, fallback: string): string {
  if (!(error instanceof HttpErrorResponse)) {return fallback;}
  const body: unknown = error.error;
  if (!body || typeof body !== 'object') {return fallback;}
  if ('detail' in body && typeof body.detail === 'string' && body.detail.trim()) {return body.detail;}
  if ('errors' in body && body.errors && typeof body.errors === 'object') {
    const messages = Object.values(body.errors).flat().filter((message: unknown): message is string => typeof message === 'string');
    if (messages.length) {return messages.join(' ');}
  }
  return fallback;
}
