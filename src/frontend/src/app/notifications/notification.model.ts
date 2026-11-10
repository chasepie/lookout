/** Mirrors `NotificationService` in src/Core/NotificationChannel.cs. The backend serializes enums as numbers. */
export enum NotificationService {
  Pushover = 0,
}

/** Mirrors `WatchEvent` in src/Core/WatchEvent.cs. */
export enum WatchEvent {
  ContentChange = 0,
  KnownFailureStreak = 1,
  Crash = 2,
  Recovery = 3,
}

/** A watch, by its name. Mirrors `WatchReference` in src/Core/NotificationChannel.cs. */
export interface WatchReference {
  id: number;
  name: string;
}

/** A Pushover channel's settings, without its credentials. Mirrors `PushoverChannelSummary`. */
export interface PushoverChannelSummary {
  retrySeconds: number;
  expireSeconds: number;
}

/** A notification channel, without its credentials. Mirrors `NotificationChannelSummary`. */
export interface NotificationChannelSummary {
  id: number;
  name: string;
  service: NotificationService;
  pushover: PushoverChannelSummary | null;
  /** The watches that send it notifications, by name. */
  watches: WatchReference[];
}

/** A Pushover channel's settings to save. A `null` token or user key keeps the saved one. Mirrors `PushoverChannelDto`. */
export interface PushoverChannelDto {
  applicationToken: string | null;
  userKey: string | null;
  retrySeconds: number;
  expireSeconds: number;
}

/** A notification channel to save. Mirrors `NotificationChannelDto` in src/Core/NotificationChannel.cs. */
export interface NotificationChannelDto {
  name: string;
  service: NotificationService;
  pushover: PushoverChannelDto;
}

/** How a subscription's notifications are sent through a Pushover channel. Mirrors `PushoverSubscriptionDto`. */
export interface PushoverSubscriptionDto {
  /** From -2, the quietest, to 2, an emergency. */
  priority: number;
  /** A Pushover sound's name, or `null` for the user's default. */
  sound: string | null;
}

/** Which of a watch's events go to a channel, and how. Mirrors `SubscriptionDto` in src/Core/Subscription.cs. */
export interface SubscriptionDto {
  notificationChannelId: number;
  events: WatchEvent[];
  pushover: PushoverSubscriptionDto | null;
}
