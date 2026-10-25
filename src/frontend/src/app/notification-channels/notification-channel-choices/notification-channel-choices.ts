import { ChangeDetectionStrategy, Component, inject, model } from '@angular/core';
import { RouterLink } from '@angular/router';
import { notificationChannelKindLabels } from '../notification-channel.model';
import { NotificationChannels } from '../notification-channels';

/** Checkboxes for choosing which Notification channels a Watch notifies. Its content, such as a save button, shows below them. */
@Component({
  selector: 'app-notification-channel-choices',
  imports: [RouterLink],
  templateUrl: './notification-channel-choices.html',
  styleUrl: './notification-channel-choices.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationChannelChoices {
  protected readonly notificationChannels = inject(NotificationChannels);
  /** The IDs of the chosen Notification channels. */
  readonly chosen = model.required<ReadonlySet<string>>();
  protected readonly kinds = notificationChannelKindLabels;

  constructor() {
    // Notification channels may have been added in Settings since the list was last read.
    this.notificationChannels.list.refresh();
  }

  protected toggle(id: string, checked: boolean) {
    this.chosen.update((chosen) => {
      const next = new Set(chosen);
      if (checked) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }
}
