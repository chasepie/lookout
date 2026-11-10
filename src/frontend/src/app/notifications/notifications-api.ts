import { HttpClient, httpResource } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { NotificationChannelDto, NotificationChannelSummary, SubscriptionDto } from './notification.model';

const channelsUrl = '/api/NotificationChannels';

/**
 * Client for the Backend's notification channels and watches' subscriptions.
 * The `*Resource` methods must be called in an injection context (e.g. a component field initializer).
 */
@Service()
export class NotificationsApi {
  private readonly http = inject(HttpClient);

  channelsResource() {
    return httpResource<NotificationChannelSummary[]>(() => channelsUrl);
  }

  createChannel(channel: NotificationChannelDto) {
    return firstValueFrom(this.http.post<NotificationChannelSummary>(channelsUrl, channel));
  }

  updateChannel(id: number, channel: NotificationChannelDto) {
    return firstValueFrom(this.http.put<NotificationChannelSummary>(`${channelsUrl}/${id}`, channel));
  }

  deleteChannel(id: number) {
    return firstValueFrom(this.http.delete(`${channelsUrl}/${id}`, { responseType: 'text' }));
  }

  /** Sends a test notification right away; fails with the service's reason if it isn't taken. */
  sendTest(id: number) {
    // JSON, so a failure's problem details come back parsed
    return firstValueFrom(this.http.post<null>(`${channelsUrl}/${id}/test`, null));
  }

  /** Makes no request while `watchId` returns `undefined`. */
  subscriptionsResource(watchId: () => string | undefined) {
    return httpResource<SubscriptionDto[]>(() => {
      const id = watchId();
      return id === undefined ? undefined : `/api/Watches/${id}/subscriptions`;
    });
  }

  /** Replaces all of the watch's subscriptions. */
  replaceSubscriptions(watchId: number | string, subscriptions: SubscriptionDto[]) {
    return firstValueFrom(this.http.put<SubscriptionDto[]>(`/api/Watches/${watchId}/subscriptions`, subscriptions));
  }
}
