import { ChangeDetectionStrategy, Component, inject, input, linkedSignal, output, signal } from '@angular/core';
import { NotificationChannelChoices } from '../../notification-channels/notification-channel-choices/notification-channel-choices';
import { NotificationChannels } from '../../notification-channels/notification-channels';
import { problemMessage } from '../../shared/problem';
import type { Watch } from '../watch.model';
import { Watches } from '../watches';

/** Changes which Notification channels an existing Watch notifies. */
@Component({
  selector: 'app-watch-notification-channels',
  imports: [NotificationChannelChoices],
  templateUrl: './watch-notification-channels.html',
  styleUrl: './watch-notification-channels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchNotificationChannels {
  private readonly watches = inject(Watches);
  private readonly notificationChannels = inject(NotificationChannels);
  readonly watch = input.required<Watch>();
  readonly saved = output();
  // The choice this component is saving, as the backend will store it, until it arrives back on the Watch. Its arrival
  // must not discard choices made meanwhile.
  private submitted: { id: string; notificationChannelIds: string[] } | undefined;
  // Follows the saved choice. A refreshed copy of the Watch, as pausing or running it brings, keeps unsaved edits.
  protected readonly chosen = linkedSignal<Watch, ReadonlySet<string>>({
    source: this.watch,
    computation: (watch, previous) => {
      if (previous?.source.id !== watch.id) {
        return new Set(watch.notificationChannelIds);
      }
      // A read that brings no change, such as one from before the save, keeps unsaved edits.
      if (sameIds(previous.source.notificationChannelIds, watch.notificationChannelIds)) {
        return previous.value;
      }
      // The first change to arrive answers the save, and is consumed so it can't match a later change elsewhere.
      // This component's own choice keeps the edits made since; anyone else's replaces them.
      const submitted = this.submitted;
      this.submitted = undefined;
      return submitted?.id === watch.id && sameIds(submitted.notificationChannelIds, watch.notificationChannelIds)
        ? previous.value
        : new Set(watch.notificationChannelIds);
    },
  });
  protected readonly saving = signal(false);
  protected readonly error = signal('');

  protected async save() {
    const id = this.watch().id;
    const notificationChannelIds = this.notificationChannels.stillListed(this.chosen());
    this.submitted = { id, notificationChannelIds };
    this.saving.set(true);
    this.error.set('');
    try {
      await this.watches.changeNotificationChannels(id, notificationChannelIds);
      this.saved.emit();
    } catch (error: unknown) {
      this.submitted = undefined;
      this.error.set(problemMessage(error, 'Could not save the Notification channels. Please try again.'));
    } finally {
      this.saving.set(false);
    }
  }
}

function sameIds(saved: string[], current: string[]) {
  return saved.length === current.length && saved.every((id) => current.includes(id));
}
