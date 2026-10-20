import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { type ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { freshRequestToken } from './request-token';
import { sessionEnded } from './session';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(
      withInterceptors([sessionEnded, freshRequestToken]),
      // Every port on a host shares cookies, so the default XSRF-TOKEN would collide with other Angular apps.
      withXsrfConfiguration({ cookieName: 'Lookout.XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
    ),
    provideRouter(routes, withComponentInputBinding()),
  ],
};
