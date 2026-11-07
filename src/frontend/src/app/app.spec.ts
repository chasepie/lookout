import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { Session } from './auth/session';
import { LiveUpdates } from './live/live-updates';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        // No hub to connect to in tests
        { provide: LiveUpdates, useValue: { state: signal('live') } },
      ],
    }).compileComponents();
  });

  async function signIn() {
    const check = TestBed.inject(Session).check();
    TestBed.inject(HttpTestingController).expectOne('/api/auth/me').flush({ username: 'owner' });
    await check;
  }

  it('links the wordmark to the watches', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const wordmark = (fixture.nativeElement as HTMLElement).querySelector('a.wordmark');
    expect(wordmark?.textContent).toContain('Lookout');
    expect(wordmark?.getAttribute('href')).toBe('/');
  });

  it('hides the navigation and sign-out button while signed out', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('nav')).toBeNull();
    expect(compiled.querySelector('button')).toBeNull();
  });

  it('shows who is signed in, whether updates are live, and a sign-out button', async () => {
    await signIn();

    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('nav')).not.toBeNull();
    expect(compiled.textContent).toContain('Signed in as owner');
    expect(compiled.querySelector('app-live-indicator')?.textContent).toContain('Live');
    expect(compiled.querySelector('button')?.textContent).toContain('Sign out');
  });

  it('says so when signing out fails, and stays signed in', async () => {
    await signIn();
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;

    compiled.querySelector('button')?.click();
    TestBed.inject(HttpTestingController).expectOne('/api/auth/logout').error(new ProgressEvent('error'));
    await fixture.whenStable();

    expect(compiled.querySelector('[role=alert]')?.textContent).toContain("Couldn't sign out");
    expect(compiled.textContent).toContain('Signed in as owner');
  });
});
