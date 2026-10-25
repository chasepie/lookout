export type NotificationChannelKind = 'discord' | 'pushover';

export const notificationChannelKindLabels: Record<NotificationChannelKind, string> = { discord: 'Discord', pushover: 'Pushover' };

/** A Notification channel as the backend lists it, which never includes its secrets. */
export interface NotificationChannel {
  id: string;
  name: string;
  kind: NotificationChannelKind;
}

/** A new Notification channel and the secrets it is sent with, of which only its kind's are used. */
export interface NewNotificationChannel {
  kind: NotificationChannelKind;
  name: string;
  webhookUrl: string;
  userKey: string;
  appToken: string;
}
