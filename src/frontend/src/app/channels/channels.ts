import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import { Session } from '../session/session';
import type { Channel, NewChannel } from './channel.model';

@Service()
export class Channels {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly list = refreshableHttpResource<Channel[]>(() => (this.session.userName() ? { url: '/api/channels' } : undefined));

  async create(channel: NewChannel) {
    // Only the chosen kind's secrets are sent.
    const { kind, name, webhookUrl, userKey, appToken } = channel;
    const body = kind === 'discord' ? { kind, name, webhookUrl } : { kind, name, userKey, appToken };
    const created = await firstValueFrom(this.http.post<Channel>('/api/channels', body));
    this.list.refresh();
    return created;
  }

  async delete(id: string) {
    await firstValueFrom(this.http.delete<void>(`/api/channels/${id}`));
    // Removed at once, so the list never shows the deleted Channel while it reloads.
    if (this.list.hasValue()) {
      this.list.update((channels) => channels?.filter((channel) => channel.id !== id));
    }
  }

  /**
   * The chosen Channels that are still listed. A choice can outlive its Channel, which once deleted elsewhere has no
   * checkbox to clear it, and would be rejected.
   */
  stillListed(chosen: Iterable<string>) {
    const listed = new Set(this.list.hasValue() ? this.list.value().map((channel) => channel.id) : []);
    return [...chosen].filter((id) => listed.has(id));
  }

  sendTest(id: string) {
    return firstValueFrom(this.http.post<void>(`/api/channels/${id}/test`, {}));
  }
}
