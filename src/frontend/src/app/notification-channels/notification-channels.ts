import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { refreshableHttpResource } from '../shared/refreshable-http-resource';
import { Session } from '../session/session';
import type { NotificationChannel, NewNotificationChannel } from './notification-channel.model';

@Service()
export class NotificationChannels {
  private readonly http = inject(HttpClient);
  private readonly session = inject(Session);
  // Loaded only while logged in, so it is fetched again for the next login.
  readonly list = refreshableHttpResource<NotificationChannel[]>(() => (this.session.userName() ? { url: '/api/notification-channels' } : undefined));

  async create(notificationChannel: NewNotificationChannel) {
    // Only the chosen kind's secrets are sent.
    const { kind, name, webhookUrl, userKey, appToken } = notificationChannel;
    const body = kind === 'discord' ? { kind, name, webhookUrl } : { kind, name, userKey, appToken };
    const created = await firstValueFrom(this.http.post<NotificationChannel>('/api/notification-channels', body));
    this.list.refresh();
    return created;
  }

  async delete(id: string) {
    await firstValueFrom(this.http.delete<void>(`/api/notification-channels/${id}`));
    // Removed at once, so the list never shows the deleted Notification channel while it reloads.
    if (this.list.hasValue()) {
      this.list.update((notificationChannels) => notificationChannels?.filter((notificationChannel) => notificationChannel.id !== id));
    }
  }

  /**
   * The chosen Notification channels that are still listed. A choice can outlive its Notification channel, which once
   * deleted elsewhere has no checkbox to clear it, and would be rejected.
   */
  stillListed(chosen: Iterable<string>) {
    const listed = new Set(this.list.hasValue() ? this.list.value().map((notificationChannel) => notificationChannel.id) : []);
    return [...chosen].filter((id) => listed.has(id));
  }

  sendTest(id: string) {
    return firstValueFrom(this.http.post<void>(`/api/notification-channels/${id}/test`, {}));
  }
}
