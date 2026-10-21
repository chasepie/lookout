import { ChangeDetectionStrategy, Component, inject, input, linkedSignal, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { channelKindLabels } from '../../channels/channel.model';
import { Channels } from '../../channels/channels';
import { problemMessage } from '../../shared/problem';
import type { Watch } from '../watch.model';
import { Watches } from '../watches';

/** Chooses which Channels a Watch notifies. */
@Component({
  selector: 'app-watch-channels',
  imports: [RouterLink],
  templateUrl: './watch-channels.html',
  styleUrl: './watch-channels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchChannels {
  private readonly watches = inject(Watches);
  protected readonly channels = inject(Channels);
  readonly watch = input.required<Watch>();
  readonly saved = output();
  // Follows the saved choice. A refreshed copy of the Watch, as pausing or running it brings, keeps unsaved edits.
  protected readonly chosen = linkedSignal<Watch, ReadonlySet<string>>({
    source: this.watch,
    computation: (watch, previous) => previous?.source.id === watch.id
      && sameChoice(previous.source.channelIds, watch.channelIds)
      ? previous.value
      : new Set(watch.channelIds),
  });
  protected readonly kinds = channelKindLabels;
  protected readonly saving = signal(false);
  protected readonly error = signal('');

  constructor() {
    // Channels may have been added in Settings since the list was last read.
    this.channels.list.refresh();
  }

  protected toggle(id: string, checked: boolean) {
    this.chosen.update((chosen) => {
      const next = new Set(chosen);
      if (checked) {next.add(id);} else {next.delete(id);}
      return next;
    });
  }

  protected async save() {
    this.saving.set(true);
    this.error.set('');
    try {
      await this.watches.changeChannels(this.watch().id, [...this.chosen()]);
      this.saved.emit();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not save the Channels. Please try again.'));
    } finally {
      this.saving.set(false);
    }
  }
}

function sameChoice(a: string[], b: string[]) {
  return a.length === b.length && a.every((id) => b.includes(id));
}
