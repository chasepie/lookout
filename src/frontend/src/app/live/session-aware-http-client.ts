import { HttpClient, HttpError, HttpRequest, HttpResponse } from '@microsoft/signalr';

/**
 * SignalR's HTTP client, reporting when the Backend refuses the session. SignalR itself wraps a refused negotiate
 * request in an error without its status code, so this is the only place the live updates connection can tell.
 */
export class SessionAwareHttpClient extends HttpClient {
  private readonly inner: HttpClient;
  private readonly onUnauthorized: () => void;

  constructor(inner: HttpClient, onUnauthorized: () => void) {
    super();
    this.inner = inner;
    this.onUnauthorized = onUnauthorized;
  }

  override async send(request: HttpRequest): Promise<HttpResponse> {
    try {
      return await this.inner.send(request);
    } catch (error) {
      if (error instanceof HttpError && error.statusCode === 401) {
        this.onUnauthorized();
      }
      throw error;
    }
  }

  override getCookieString(url: string): string {
    return this.inner.getCookieString(url);
  }
}
