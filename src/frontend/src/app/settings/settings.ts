import { KeyValuePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, hidden, maxLength, pattern, required, submit } from '@angular/forms/signals';
import { type NotificationChannel, type NewNotificationChannel, notificationChannelKindLabels } from '../notification-channels/notification-channel.model';
import { NotificationChannels } from '../notification-channels/notification-channels';
import { problemMessage } from '../shared/problem';
import { InstallSettings } from './install-settings';
import { RetentionSettings } from './retention-settings/retention-settings';
import { TimeZoneSettings } from './time-zone-settings/time-zone-settings';
import { VpnStatus } from './vpn-status/vpn-status';

type NotificationChannelAction = 'testing' | 'deleting';

const emptyNotificationChannel = (): NewNotificationChannel => ({ kind: 'discord', name: '', webhookUrl: '', userKey: '', appToken: '' });

/**
 * Lookout's settings: the Notification channels Watches can notify, how long Runs are kept, the time zone Schedules use,
 * and the VPN's status.
 */
@Component({
  selector: 'app-settings',
  imports: [FormField, KeyValuePipe, RetentionSettings, TimeZoneSettings, VpnStatus],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Settings {
  protected readonly notificationChannels = inject(NotificationChannels);
  protected readonly model = signal(emptyNotificationChannel());
  protected readonly notificationChannelForm = form(this.model, (fields) => {
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
  protected readonly kinds = notificationChannelKindLabels;
  protected readonly error = signal('');
  protected readonly notice = signal('');
  /** The action in progress on each Notification channel. */
  protected readonly busy = signal(new Map<string, NotificationChannelAction>());
  /** The Notification channel whose deletion is being confirmed, if any. */
  protected readonly confirmingDelete = signal<string | undefined>(undefined);

  constructor() {
    // Notification channels and settings may have changed in another tab since they were last read.
    this.notificationChannels.list.refresh();
    inject(InstallSettings).resource.refresh();
  }

  protected async addNotificationChannel() {
    await submit(this.notificationChannelForm, async () => {
      this.clearMessages();
      try {
        const created = await this.notificationChannels.create(this.model());
        // The kind stays chosen, as the next Notification channel is likely another of the same kind.
        this.notificationChannelForm().reset({ ...emptyNotificationChannel(), kind: this.model().kind });
        this.notice.set(`${created.name} added.`);
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not add the Notification channel. Check the fields and try again.'));
      }
    });
  }

  protected async sendTest(notificationChannel: NotificationChannel) {
    await this.perform(notificationChannel, 'testing', 'Could not send the test notification. Please try again.', async () => {
      await this.notificationChannels.sendTest(notificationChannel.id);
      this.notice.set(`Test notification sent to ${notificationChannel.name}.`);
    });
  }

  protected async delete(notificationChannel: NotificationChannel) {
    await this.perform(notificationChannel, 'deleting', 'Could not delete the Notification channel. Please try again.', async () => {
      await this.notificationChannels.delete(notificationChannel.id);
      this.confirmingDelete.set(undefined);
      this.notice.set(`${notificationChannel.name} deleted.`);
    });
  }

  private async perform(notificationChannel: NotificationChannel, action: NotificationChannelAction, failure: string, work: () => Promise<void>) {
    this.busy.update((actions) => new Map(actions).set(notificationChannel.id, action));
    this.clearMessages();
    try {
      await work();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, failure));
    } finally {
      this.busy.update((actions) => {
        const next = new Map(actions);
        next.delete(notificationChannel.id);
        return next;
      });
    }
  }

  protected showNotice(message: string) {
    this.clearMessages();
    this.notice.set(message);
  }

  protected showError(message: string) {
    this.clearMessages();
    this.error.set(message);
  }

  private clearMessages() {
    this.error.set('');
    this.notice.set('');
  }
}
