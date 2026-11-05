import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth-interceptor';
import { Session, csrfHeader } from './session';

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let session: Session;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    session = TestBed.inject(Session);
  });

  afterEach(() => backend.verify());

  it('adds the CSRF header to API requests', () => {
    void firstValueFrom(http.post('/api/Watches/1/runs', null));

    expect(backend.expectOne('/api/Watches/1/runs').request.headers.get(csrfHeader)).toBe('1');
  });

  it('leaves other requests alone', () => {
    void firstValueFrom(http.get('/assets/thing.json'));

    expect(backend.expectOne('/assets/thing.json').request.headers.has(csrfHeader)).toBe(false);
  });

  it('ends the session when an API request is rejected as signed out', async () => {
    const expired = vi.spyOn(session, 'expired').mockImplementation(() => undefined);
    const request = firstValueFrom(http.get('/api/Watches'));

    backend.expectOne('/api/Watches').flush('', { status: 401, statusText: 'Unauthorized' });

    await expect(request).rejects.toBeTruthy();
    expect(expired).toHaveBeenCalledOnce();
  });

  it("leaves the auth endpoints' 401s to the caller", async () => {
    const expired = vi.spyOn(session, 'expired').mockImplementation(() => undefined);
    const request = firstValueFrom(http.post('/api/auth/login', { username: 'owner', password: 'wrong' }));

    backend.expectOne('/api/auth/login').flush('Username or password is incorrect.', {
      status: 401,
      statusText: 'Unauthorized',
    });

    await expect(request).rejects.toBeTruthy();
    expect(expired).not.toHaveBeenCalled();
  });
});
