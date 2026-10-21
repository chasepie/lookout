import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, hidden, maxLength, pattern, required, submit } from '@angular/forms/signals';
import { type Channel, type NewChannel, channelKindLabels } from '../channels/channel.model';
import { Channels } from '../channels/channels';
import { problemMessage } from '../shared/problem';

type ChannelAction = 'testing' | 'deleting';

const emptyChannel = (): NewChannel => ({ kind: 'discord', name: '', webhookUrl: '', userKey: '', appToken: '' });

/** Lookout's settings: the Channels Watches can notify. */
@Component({
  selector: 'app-settings',
  imports: [FormField],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Settings {
  protected readonly channels = inject(Channels);
  protected readonly model = signal(emptyChannel());
  protected readonly channelForm = form(this.model, (fields) => {
    required(fields.name);
    pattern(fields.name, /\S/);
    maxLength(fields.name, 100);
    // Only the chosen kind's fields are shown and checked.
    hidden(fields.webhookUrl, { when: (field) => field.valueOf(fields.kind) !== 'discord' });
    required(fields.webhookUrl);
    pattern(fields.webhookUrl, /^\s*https?:\/\/\S+\s*$/i);
    for (const secret of [fields.userKey, fields.appToken]) {
      hidden(secret, { when: (field) => field.valueOf(fields.kind) !== 'pushover' });
      required(secret);
    }
  });
  protected readonly kinds = channelKindLabels;
  protected readonly error = signal('');
  protected readonly notice = signal('');
  /** The action in progress on each Channel. */
  protected readonly busy = signal(new Map<string, ChannelAction>());
  /** The Channel whose deletion is being confirmed, if any. */
  protected readonly confirmingDelete = signal<string | undefined>(undefined);

  constructor() {
    // Channels may have changed in another tab since the list was last read.
    this.channels.list.refresh();
  }

  protected async addChannel() {
    await submit(this.channelForm, async () => {
      this.clearMessages();
      try {
        const created = await this.channels.create(this.model());
        // The kind stays chosen, as the next Channel is likely another of the same kind.
        this.channelForm().reset({ ...emptyChannel(), kind: this.model().kind });
        this.notice.set(`${created.name} added.`);
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not add the Channel. Check the fields and try again.'));
      }
    });
  }

  protected async sendTest(channel: Channel) {
    await this.perform(channel, 'testing', 'Could not send the test notification. Please try again.', async () => {
      await this.channels.sendTest(channel.id);
      this.notice.set(`Test notification sent to ${channel.name}.`);
    });
  }

  protected async delete(channel: Channel) {
    await this.perform(channel, 'deleting', 'Could not delete the Channel. Please try again.', async () => {
      await this.channels.delete(channel.id);
      this.confirmingDelete.set(undefined);
      this.notice.set(`${channel.name} deleted.`);
    });
  }

  private async perform(channel: Channel, action: ChannelAction, failure: string, work: () => Promise<void>) {
    this.busy.update((actions) => new Map(actions).set(channel.id, action));
    this.clearMessages();
    try {
      await work();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, failure));
    } finally {
      this.busy.update((actions) => {
        const next = new Map(actions);
        next.delete(channel.id);
        return next;
      });
    }
  }

  private clearMessages() {
    this.error.set('');
    this.notice.set('');
  }
}
