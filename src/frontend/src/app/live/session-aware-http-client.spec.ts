import { HttpClient, HttpError, HttpRequest, HttpResponse } from '@microsoft/signalr';
import { SessionAwareHttpClient } from './session-aware-http-client';

class FakeHttpClient extends HttpClient {
  constructor(private readonly respond: () => Promise<HttpResponse>) {
    super();
  }

  override send(): Promise<HttpResponse> {
    return this.respond();
  }
}

describe('SessionAwareHttpClient', () => {
  const request: HttpRequest = { method: 'POST', url: '/api/hubs/updates/negotiate' };

  it('reports a refused session and still fails the request', async () => {
    const onUnauthorized = vi.fn();
    const client = new SessionAwareHttpClient(
      new FakeHttpClient(() => Promise.reject(new HttpError('Unauthorized', 401))),
      onUnauthorized,
    );

    await expect(client.send(request)).rejects.toBeInstanceOf(HttpError);
    expect(onUnauthorized).toHaveBeenCalledOnce();
  });

  it('leaves other failures alone', async () => {
    const onUnauthorized = vi.fn();
    const client = new SessionAwareHttpClient(
      new FakeHttpClient(() => Promise.reject(new HttpError('Bad Gateway', 502))),
      onUnauthorized,
    );

    await expect(client.send(request)).rejects.toBeInstanceOf(HttpError);
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('passes responses through', async () => {
    const response = new HttpResponse(200, 'OK', '{}');
    const client = new SessionAwareHttpClient(new FakeHttpClient(() => Promise.resolve(response)), vi.fn());

    expect(await client.send(request)).toBe(response);
  });
});
