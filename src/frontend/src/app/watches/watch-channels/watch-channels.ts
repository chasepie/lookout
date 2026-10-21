import { ChangeDetectionStrategy, Component, inject, input, linkedSignal, output, signal } from '@angular/core';
import { ChannelChoices } from '../../channels/channel-choices/channel-choices';
import { Channels } from '../../channels/channels';
import { problemMessage } from '../../shared/problem';
import type { Watch } from '../watch.model';
import { Watches } from '../watches';

/** Changes which Channels an existing Watch notifies. */
@Component({
  selector: 'app-watch-channels',
  imports: [ChannelChoices],
  templateUrl: './watch-channels.html',
  styleUrl: './watch-channels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchChannels {
  private readonly watches = inject(Watches);
  private readonly channels = inject(Channels);
  readonly watch = input.required<Watch>();
  readonly saved = output();
  // The choice this component is saving, as the backend will store it, until it arrives back on the Watch. Its arrival
  // must not discard choices made meanwhile.
  private submitted: { id: string; channelIds: string[] } | undefined;
  // Follows the saved choice. A refreshed copy of the Watch, as pausing or running it brings, keeps unsaved edits.
  protected readonly chosen = linkedSignal<Watch, ReadonlySet<string>>({
    source: this.watch,
    computation: (watch, previous) => {
      if (previous?.source.id !== watch.id) {
        return new Set(watch.channelIds);
      }
      const submitted = this.submitted;
      if (submitted?.id === watch.id && sameIds(submitted.channelIds, watch.channelIds)) {
        // Consumed once, so a later change elsewhere back to this same choice is shown.
        this.submitted = undefined;
        return previous.value;
      }
      return sameIds(previous.source.channelIds, watch.channelIds) ? previous.value : new Set(watch.channelIds);
    },
  });
  protected readonly saving = signal(false);
  protected readonly error = signal('');

  protected async save() {
    const id = this.watch().id;
    const channelIds = this.channels.stillListed(this.chosen());
    this.submitted = { id, channelIds };
    this.saving.set(true);
    this.error.set('');
    try {
      await this.watches.changeChannels(id, channelIds);
      this.saved.emit();
    } catch (error: unknown) {
      this.submitted = undefined;
      this.error.set(problemMessage(error, 'Could not save the Channels. Please try again.'));
    } finally {
      this.saving.set(false);
    }
  }
}

function sameIds(saved: string[], current: string[]) {
  return saved.length === current.length && saved.every((id) => current.includes(id));
}
