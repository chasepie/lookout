import { inject } from '@angular/core';
import { CanActivateChildFn, CanActivateFn, Router } from '@angular/router';
import { Session } from './session';

/** Sends the Owner to the login page, and back here afterwards, unless signed in. */
export const ownerGuard: CanActivateChildFn = async (_route, state) => {
  const router = inject(Router);
  try {
    if (await inject(Session).check()) {
      return true;
    }
  } catch {
    // The Backend is unreachable. The login page says so when signing in is tried.
  }
  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** Skips the login page when already signed in. */
export const signedOutGuard: CanActivateFn = async (route) => {
  try {
    if (await inject(Session).check()) {
      return inject(Router).parseUrl(safeReturnUrl(route.queryParamMap.get('returnUrl')));
    }
  } catch {
    // Unknown, so show the login page
  }
  return true;
};

/**
 * Where to go after signing in: the requested page if it's one of the app's own, otherwise the watch list.
 * Only app-relative paths are accepted, so a crafted link can't send the Owner to another site.
 */
export function safeReturnUrl(returnUrl: string | null): string {
  return returnUrl?.startsWith('/') && !returnUrl.startsWith('//') && !returnUrl.startsWith('/login')
    ? returnUrl
    : '/';
}
