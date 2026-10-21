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
  // Follows the saved choice. A refreshed copy of the Watch, as pausing or running it brings, keeps unsaved edits.
  protected readonly chosen = linkedSignal<Watch, ReadonlySet<string>>({
    source: this.watch,
    computation: (watch, previous) => previous?.source.id === watch.id
      && sameIds(previous.source.channelIds, watch.channelIds)
      ? previous.value
      : new Set(watch.channelIds),
  });
  protected readonly saving = signal(false);
  protected readonly error = signal('');

  protected async save() {
    this.saving.set(true);
    this.error.set('');
    try {
      await this.watches.changeChannels(this.watch().id, this.channels.stillListed(this.chosen()));
      this.saved.emit();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not save the Channels. Please try again.'));
    } finally {
      this.saving.set(false);
    }
  }
}

function sameIds(saved: string[], current: string[]) {
  return saved.length === current.length && saved.every((id) => current.includes(id));
}
