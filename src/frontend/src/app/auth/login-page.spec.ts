import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { answer, nextRequest, request, textOf } from '../testing/fixtures';
import { LoginPage } from './login-page';
import { Session } from './session';

@Component({ template: '' })
class Elsewhere {}

describe('LoginPage', () => {
  let page: HTMLElement;

  async function open(url = '/login') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'login', component: LoginPage },
            { path: '**', component: Elsewhere },
          ],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, LoginPage);
    page = harness.routeNativeElement!;
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const stable = () => TestBed.inject(ApplicationRef).whenStable();

  function type(id: string, value: string) {
    const input = page.querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  async function signIn(username = 'owner', password = 'correct horse battery staple') {
    type('username', username);
    type('password', password);
    page.querySelector('form')!.dispatchEvent(new Event('submit'));
    await stable();
  }

  /** Waits for the message the submit action shows once it settles. */
  const alertText = () =>
    vi.waitFor(async () => {
      await stable();
      const text = textOf(page.querySelector('.callout[role=alert]'));
      expect(text).not.toBe('');
      return text;
    });

  it('signs in and goes back to the page the Owner asked for', async () => {
    await open('/login?returnUrl=%2Fvpn');

    await signIn();
    const login = await nextRequest(request('POST', '/api/auth/login'));
    expect(login.request.body).toEqual({ username: 'owner', password: 'correct horse battery staple' });
    login.flush({ username: 'owner' });

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/vpn'));
    expect(TestBed.inject(Session).username()).toBe('owner');
  });

  it('asks for both fields before trying', async () => {
    await open();

    page.querySelector('form')!.dispatchEvent(new Event('submit'));
    await stable();

    expect(textOf(page.querySelector('#username-error'))).toBe('Enter your username.');
    expect(textOf(page.querySelector('#password-error'))).toBe('Enter your password.');
    TestBed.inject(HttpTestingController).expectNone(request('POST', '/api/auth/login'));
  });

  it.each([
    [401, 'Username or password is incorrect.', 'Username or password is incorrect.'],
    [429, '', 'Too many sign-in attempts. Wait a minute, then try again.'],
  ])('explains a %i', async (status, body, message) => {
    await open();

    await signIn();
    (await nextRequest(request('POST', '/api/auth/login'))).flush(body, { status, statusText: 'Error' });

    expect(await alertText()).toBe(message);
  });

  it("says when Lookout can't be reached", async () => {
    await open();

    await signIn();
    (await nextRequest(request('POST', '/api/auth/login'))).error(new ProgressEvent('error'));

    expect(await alertText()).toBe("Lookout can't be reached. Check that it's running, then try again.");
  });

  it('goes to the watch list instead of another site', async () => {
    await open('/login?returnUrl=https%3A%2F%2Fevil.example%2F');

    await signIn();
    await answer(request('POST', '/api/auth/login'), { username: 'owner' });

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/'));
  });
});
