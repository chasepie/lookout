import { HttpClient, HttpErrorResponse, type HttpInterceptorFn, httpResource } from '@angular/common/http';
import { Service, computed, inject } from '@angular/core';
import { catchError, firstValueFrom, throwError } from 'rxjs';

export interface SessionStatus {
  accountExists: boolean;
  userName: string | null;
}

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
    await firstValueFrom(this.http.post('/api/session', { userName, password }));
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
    // A rejected login is answered on the login form; it doesn't end a session.
    const isLogin = request.method === 'POST' && request.url === '/api/session';
    if (error instanceof HttpErrorResponse && error.status === 401 && !isLogin) {
      session.status.reload();
    }
    return throwError(() => error);
  }));
};
