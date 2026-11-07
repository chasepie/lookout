import { OverlayContainer } from '@angular/cdk/overlay';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth-interceptor';
import { MainOverlayContainer } from './shared/main-overlay-container';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([authInterceptor])),
    { provide: OverlayContainer, useClass: MainOverlayContainer },
    provideRouter(
      routes,
      withComponentInputBinding(),
      // The run panel is a child of the watch page, and needs the watch's `id` as well as its own `runId`
      withRouterConfig({ paramsInheritanceStrategy: 'always' }),
    ),
  ],
};
