import { HttpClient, HttpContextToken, HttpErrorResponse, type HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

/** The problem type the backend answers with when a request's token is missing or stale. */
const REJECTED_REQUEST_TOKEN = 'urn:lookout:problem:request-token';

/** Marks a request already retried with a fresh request token, so it is retried only once. */
const RETRIED_WITH_FRESH_TOKEN = new HttpContextToken(() => false);

/**
 * Retries a request once when the backend rejected its request token, after the session status issues a fresh one.
 * The token can go stale or missing, for example when another app on the same host overwrites cookies. The backend
 * checks the token before acting, so retrying can't repeat a change.
 */
export const freshRequestToken: HttpInterceptorFn = (request, next) => {
  const http = inject(HttpClient);
  return next(request).pipe(catchError((error: unknown) => {
    if (!isRejectedRequestToken(error) || request.context.get(RETRIED_WITH_FRESH_TOKEN)) {
      return throwError(() => error);
    }
    request.context.set(RETRIED_WITH_FRESH_TOKEN, true);
    // Through HttpClient rather than next, so the XSRF interceptor attaches the fresh token.
    return http.get('/api/session').pipe(switchMap(() => http.request(request)));
  }));
};

function isRejectedRequestToken(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) {return false;}
  const body: unknown = error.error;
  return !!body && typeof body === 'object' && 'type' in body && body.type === REJECTED_REQUEST_TOKEN;
}
