import { ChangeDetectionStrategy, Component, inject, model } from '@angular/core';
import { RouterLink } from '@angular/router';
import { channelKindLabels } from '../channel.model';
import { Channels } from '../channels';

/** Checkboxes for choosing which Channels a Watch notifies. Its content, such as a save button, shows below them. */
@Component({
  selector: 'app-channel-choices',
  imports: [RouterLink],
  templateUrl: './channel-choices.html',
  styleUrl: './channel-choices.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelChoices {
  protected readonly channels = inject(Channels);
  /** The IDs of the chosen Channels. */
  readonly chosen = model.required<ReadonlySet<string>>();
  protected readonly kinds = channelKindLabels;

  constructor() {
    // Channels may have been added in Settings since the list was last read.
    this.channels.list.refresh();
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
