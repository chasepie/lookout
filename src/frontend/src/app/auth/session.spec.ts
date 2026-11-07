import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { request } from '../testing/fixtures';
import { Session } from './session';

@Component({ template: '' })
class AnyPage {}

describe('Session', () => {
  let session: Session;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '**', component: AnyPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    session = TestBed.inject(Session);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('asks the Backend once, however many pages check at the same time', async () => {
    const first = session.check();
    const second = session.check();
    backend.expectOne(request('GET', '/api/auth/me')).flush({ username: 'owner' });

    expect(await first).toBe(true);
    expect(await second).toBe(true);
    expect(await session.check()).toBe(true);
    expect(session.username()).toBe('owner');
  });

  it('is signed out when the Backend refuses the session', async () => {
    const check = session.check();
    backend.expectOne(request('GET', '/api/auth/me')).flush('', { status: 401, statusText: 'Unauthorized' });

    expect(await check).toBe(false);
    expect(session.signedIn()).toBe(false);
  });

  it("asks again next time when the Backend couldn't be reached", async () => {
    const check = session.check();
    backend.expectOne(request('GET', '/api/auth/me')).error(new ProgressEvent('error'));
    await expect(check).rejects.toBeTruthy();

    const retry = session.check();
    backend.expectOne(request('GET', '/api/auth/me')).flush({ username: 'owner' });
    expect(await retry).toBe(true);
  });

  it('signs out and goes to the login page', async () => {
    const signIn = session.signIn('owner', 'secret');
    backend.expectOne(request('POST', '/api/auth/login')).flush({ username: 'owner' });
    await signIn;

    const signOut = session.signOut();
    backend.expectOne(request('POST', '/api/auth/logout')).flush(null);
    await signOut;

    expect(session.signedIn()).toBe(false);
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it('sends the Owner to sign in when the session ends, and back here afterwards', async () => {
    const harness = await RouterTestingHarness.create('/watches/3');

    session.expired();
    await harness.fixture.whenStable();

    expect(session.signedIn()).toBe(false);
    expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fwatches%2F3');
  });
});
