import { safeReturnUrl } from './auth-guards';

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
