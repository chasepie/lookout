import { HttpClient, HttpContext, HttpContextToken, HttpErrorResponse, type HttpInterceptorFn, httpResource } from '@angular/common/http';
import { Service, computed, inject } from '@angular/core';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import type { SessionStatus } from './session.model';

/** Marks a request whose 401 its caller answers itself, so it doesn't end the session. */
const ANSWERS_UNAUTHORIZED = new HttpContextToken(() => false);

/** Whether Lookout's single account exists and is logged in, which decides the screen the app shows. */
@Service()
export class Session {
  private readonly http = inject(HttpClient);
  readonly status = httpResource<SessionStatus>(() => '/api/session');
  readonly userName = computed(() => (this.status.hasValue() ? this.status.value().userName : null));

  async setUp(userName: string, password: string) {
    await firstValueFrom(this.http.post('/api/setup', { userName, password }));
    this.status.reload();
  }

  async logIn(userName: string, password: string) {
    // A rejected login is answered on the login form.
    const context = new HttpContext().set(ANSWERS_UNAUTHORIZED, true);
    await firstValueFrom(this.http.post('/api/session', { userName, password }, { context }));
    this.status.reload();
  }

  async logOut() {
    try {
      await firstValueFrom(this.http.delete('/api/session'));
    } finally {
      this.status.reload();
    }
  }
}

/** Sends the user back to login when the backend no longer accepts their session, such as after a logout elsewhere. */
export const sessionEnded: HttpInterceptorFn = (request, next) => {
  const session = inject(Session);
  return next(request).pipe(catchError((error: unknown) => {
    if (error instanceof HttpErrorResponse && error.status === 401 && !request.context.get(ANSWERS_UNAUTHORIZED)) {
      session.status.reload();
    }
    return throwError(() => error);
  }));
};
