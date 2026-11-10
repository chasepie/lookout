import { HttpErrorResponse } from '@angular/common/http';

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

/** ASP.NET's validation problems come back as a map of field to messages; this flattens them into sentences. */
export function describeSaveError(error: unknown): string {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object' && 'errors' in error.error) {
    const errors = (error.error as { errors: Record<string, string[]> }).errors;
    return Object.values(errors).flat().join('\n');
  }
  return describeError(error);
}

/** A problem's detail, which says what went wrong, or else the error as `describeError` reads it. */
export function describeProblem(error: unknown): string {
  if (error && typeof error === 'object' && 'error' in error) {
    const body = error.error;
    if (body && typeof body === 'object' && 'detail' in body && typeof body.detail === 'string') {
      return body.detail;
    }
  }
  return describeError(error);
}
