import { NotificationChannelSummary, SubscriptionDto, WatchEvent } from './notification.model';

/** A subscription as the watch form edits it. */
export interface SubscriptionModel {
  /** Tells rows apart while they're added and removed. */
  key: number;
  channelId: number;
  events: WatchEvent[];
  priority: number;
  /** Blank for the user's default. */
  sound: string;
}

/** Each event, in the order the form lists them, with how it reads as a checkbox and in a sentence. */
export const watchEvents = [
  { event: WatchEvent.ContentChange, label: 'Content changes', plural: 'content changes' },
  { event: WatchEvent.KnownFailureStreak, label: 'Known-failure streaks', plural: 'known-failure streaks' },
  { event: WatchEvent.Crash, label: 'Crashes', plural: 'crashes' },
  { event: WatchEvent.Recovery, label: 'Recoveries', plural: 'recoveries' },
];

/** Pushover's priorities, quietest first. */
export const priorities = [
  { value: -2, label: 'Lowest: no alert' },
  { value: -1, label: 'Low: no sound or vibration' },
  { value: 0, label: 'Normal' },
  { value: 1, label: 'High: ignores quiet hours' },
  { value: 2, label: 'Emergency: repeats until acknowledged' },
];

let nextKey = 1;

export function newSubscription(channelId: number): SubscriptionModel {
  return { key: nextKey++, channelId, events: [], priority: 0, sound: '' };
}

export function toSubscriptionModels(subscriptions: SubscriptionDto[]): SubscriptionModel[] {
  return subscriptions.map((s) => ({
    key: nextKey++,
    channelId: s.notificationChannelId,
    events: s.events,
    priority: s.pushover?.priority ?? 0,
    sound: s.pushover?.sound ?? '',
  }));
}

export function toSubscriptionDtos(subscriptions: SubscriptionModel[]): SubscriptionDto[] {
  return subscriptions.map((s) => ({
    notificationChannelId: s.channelId,
    events: s.events,
    pushover: { priority: s.priority, sound: s.sound.trim() || null },
  }));
}

/**
 * What's wrong with each subscription, or `null` where nothing is: one whose channel is gone, one that sends nothing, or
 * one that sends an event a subscription above it already sends to the same channel.
 */
/** Said of a subscription whose channel isn't among the channels, which happens when it's deleted meanwhile. */
export const deletedChannelProblem = 'This channel has been deleted. Choose another, or remove this notification.';

export function subscriptionProblems(
  subscriptions: SubscriptionModel[],
  channels: NotificationChannelSummary[],
): (string | null)[] {
  const sent = new Set<string>();
  return subscriptions.map((subscription) => {
    if (!channels.some((c) => c.id === subscription.channelId)) {
      return deletedChannelProblem;
    }
    if (subscription.events.length === 0) {
      return 'Choose at least one event to send.';
    }
    const repeated = subscription.events.filter((e) => sent.has(`${subscription.channelId}:${e}`));
    subscription.events.forEach((e) => sent.add(`${subscription.channelId}:${e}`));
    if (repeated.length === 0) {
      return null;
    }
    const channel = channels.find((c) => c.id === subscription.channelId)?.name ?? 'That channel';
    const events = listFormat.format(watchEvents.filter((e) => repeated.includes(e.event)).map((e) => e.plural));
    return `${channel} already gets ${events} from this watch.`;
  });
}

const listFormat = new Intl.ListFormat('en', { type: 'conjunction' });
