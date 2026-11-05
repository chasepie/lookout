import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { Session } from './auth/session';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('should render the title', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Lookout');
  });

  it('hides the navigation and sign-out button while signed out', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('nav')).toBeNull();
    expect(compiled.querySelector('button')).toBeNull();
  });

  it('shows who is signed in and a sign-out button', async () => {
    const check = TestBed.inject(Session).check();
    TestBed.inject(HttpTestingController).expectOne('/api/auth/me').flush({ username: 'owner' });
    await check;

    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('nav')).not.toBeNull();
    expect(compiled.textContent).toContain('Signed in as owner');
    expect(compiled.querySelector('button')?.textContent).toContain('Sign out');
  });

  it('says so when signing out fails, and stays signed in', async () => {
    const backend = TestBed.inject(HttpTestingController);
    const check = TestBed.inject(Session).check();
    backend.expectOne('/api/auth/me').flush({ username: 'owner' });
    await check;
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;

    compiled.querySelector('button')?.click();
    backend.expectOne('/api/auth/logout').error(new ProgressEvent('error'));
    await fixture.whenStable();

    expect(compiled.querySelector('[role=alert]')?.textContent).toContain("Couldn't sign out");
    expect(compiled.textContent).toContain('Signed in as owner');
  });
});
