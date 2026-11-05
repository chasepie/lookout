import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { Session, csrfHeader } from './session';

/**
 * Adds the CSRF header to every API request, and sends the Owner to the login page when the Backend says the session
 * has ended. The auth endpoints handle their own 401s: they mean wrong credentials or signed out.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/')) {
    return next(request);
  }

  const session = inject(Session);
  return next(request.clone({ setHeaders: { [csrfHeader]: '1' } })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !request.url.startsWith('/api/auth/')) {
        session.expired();
      }
      return throwError(() => error);
    }),
  );
};
