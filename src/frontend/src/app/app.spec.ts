import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
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

describe('App focus', () => {
  @Component({ template: '<h1>A page</h1>' })
  class AnyPage {}

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([
          { path: 'watches/:id', component: AnyPage, children: [{ path: 'runs/:runId', component: AnyPage }] },
          { path: '**', component: AnyPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LiveUpdates, useValue: { state: signal('live') } },
      ],
    }).compileComponents();
  });

  it('moves focus to the content on a new page, but not when a run opens beside the log', async () => {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const main = (fixture.nativeElement as HTMLElement).querySelector('main')!;
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    await router.navigateByUrl('/');
    await fixture.whenStable();

    await router.navigateByUrl('/watches/1');
    await fixture.whenStable();
    expect(document.activeElement).toBe(main);

    (document.activeElement as HTMLElement).blur();
    await router.navigateByUrl('/watches/1/runs/2');
    await fixture.whenStable();
    expect(document.activeElement).not.toBe(main);

    (fixture.nativeElement as HTMLElement).remove();
  });

  it('skips to the content of the page it is on', async () => {
    const fixture = TestBed.createComponent(App);
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    await TestBed.inject(Router).navigateByUrl('/watches/1');
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    const skip = element.querySelector<HTMLAnchorElement>('a.skip')!;
    const followed = skip.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

    expect(followed).toBe(false);
    expect(document.activeElement).toBe(element.querySelector('main'));
    expect(TestBed.inject(Router).url).toBe('/watches/1');
    element.remove();
  });
});
