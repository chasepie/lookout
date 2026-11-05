/** Turns an HTTP error into something readable. */
export function describeError(error: unknown): string {
  if (error && typeof error === 'object' && 'error' in error) {
    const body = error.error;
    if (typeof body === 'string' && body) {
      return body;
    }
    if (body && typeof body === 'object') {
      return JSON.stringify(body, null, 2);
    }
  }
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}
