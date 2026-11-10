import { Component, computed, input, model } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NotificationChannelSummary, WatchEvent } from './notification.model';
import {
  SubscriptionModel,
  deletedChannelProblem,
  newSubscription,
  priorities,
  subscriptionProblems,
  watchEvents,
} from './subscriptions';

/** A watch's subscriptions: which of its events go to which notification channel, and how. */
@Component({
  selector: 'app-subscriptions-editor',
  imports: [RouterLink],
  styleUrl: './subscriptions-editor.scss',
  templateUrl: './subscriptions-editor.html',
})
export class SubscriptionsEditor {
  readonly channels = input.required<NotificationChannelSummary[]>();
  readonly subscriptions = model.required<SubscriptionModel[]>();
  /** Whether to show what's wrong, once the Owner has tried to save. */
  readonly showProblems = input(false);

  protected readonly watchEvents = watchEvents;
  protected readonly priorities = priorities;
  protected readonly deletedChannelProblem = deletedChannelProblem;
  protected readonly problems = computed(() => subscriptionProblems(this.subscriptions(), this.channels()));

  protected add() {
    this.subscriptions.update((list) => [...list, newSubscription(this.channels()[0].id)]);
  }

  protected remove(key: number) {
    this.subscriptions.update((list) => list.filter((s) => s.key !== key));
  }

  protected setChannel(key: number, event: Event) {
    const channelId = Number((event.target as HTMLSelectElement).value);
    this.change(key, (s) => ({ ...s, channelId }));
  }

  protected setPriority(key: number, event: Event) {
    const priority = Number((event.target as HTMLSelectElement).value);
    this.change(key, (s) => ({ ...s, priority }));
  }

  protected setSound(key: number, event: Event) {
    const sound = (event.target as HTMLInputElement).value;
    this.change(key, (s) => ({ ...s, sound }));
  }

  protected toggle(key: number, watchEvent: WatchEvent, event: Event) {
    const on = (event.target as HTMLInputElement).checked;
    this.change(key, (s) => {
      const events = new Set(s.events);
      if (on) {
        events.add(watchEvent);
      } else {
        events.delete(watchEvent);
      }
      // In the order the form lists them, which is the events' own
      return { ...s, events: watchEvents.map((e) => e.event).filter((e) => events.has(e)) };
    });
  }

  private change(key: number, update: (subscription: SubscriptionModel) => SubscriptionModel) {
    this.subscriptions.update((list) => list.map((s) => (s.key === key ? update(s) : s)));
  }
}
