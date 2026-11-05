import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  convertToParamMap,
  provideRouter,
} from '@angular/router';
import { safeReturnUrl, signedOutGuard } from './auth-guards';
import { Session } from './session';

describe('safeReturnUrl', () => {
  it('returns to a page of the app', () => {
    expect(safeReturnUrl('/watches/3?tab=runs')).toBe('/watches/3?tab=runs');
  });

  it.each([
    ['nothing', null],
    ['an absolute URL', 'https://evil.example/'],
    ['a protocol-relative URL', '//evil.example/'],
    ['a relative path', 'watches/3'],
    ['the login page', '/login?returnUrl=%2F'],
  ])('goes to the watch list instead of %s', (_name, returnUrl) => {
    expect(safeReturnUrl(returnUrl)).toBe('/');
  });
});

describe('signedOutGuard', () => {
  function runGuard(signedIn: boolean, returnUrl: string | null) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: Session, useValue: { check: () => Promise.resolve(signedIn) } }],
    });
    const route = { queryParamMap: convertToParamMap(returnUrl === null ? {} : { returnUrl }) } as ActivatedRouteSnapshot;
    return TestBed.runInInjectionContext(() => signedOutGuard(route, {} as RouterStateSnapshot));
  }

  it('sends a signed-in Owner on to the page they asked for', async () => {
    const result = await runGuard(true, '/vpn');

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/vpn');
  });

  it('shows the login page while signed out', async () => {
    expect(await runGuard(false, '/vpn')).toBe(true);
  });
});
