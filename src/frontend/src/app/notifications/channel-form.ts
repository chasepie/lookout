import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { FormField, form, max, min, required, submit } from '@angular/forms/signals';
import { describeError } from '../shared/describe-error';
import { NotificationChannelDto, NotificationChannelSummary, NotificationService } from './notification.model';
import { NotificationsApi } from './notifications-api';

interface ChannelFormModel {
  name: string;
  applicationToken: string;
  userKey: string;
  retrySeconds: number;
  expireSeconds: number;
}

/** Pushover's limits on how an emergency notification repeats. */
const minRetrySeconds = 30;
const maxExpireSeconds = 3 * 60 * 60;

function toModel(channel: NotificationChannelSummary | null): ChannelFormModel {
  return {
    name: channel?.name ?? '',
    // Never sent back, so blank, which keeps the saved ones
    applicationToken: '',
    userKey: '',
    retrySeconds: channel?.pushover?.retrySeconds ?? 60,
    expireSeconds: channel?.pushover?.expireSeconds ?? 3600,
  };
}

function toDto(value: ChannelFormModel): NotificationChannelDto {
  return {
    name: value.name.trim(),
    service: NotificationService.Pushover,
    pushover: {
      applicationToken: value.applicationToken.trim() || null,
      userKey: value.userKey.trim() || null,
      retrySeconds: value.retrySeconds,
      expireSeconds: value.expireSeconds,
    },
  };
}

/** Sets up a Pushover channel, or edits one, whose credentials it keeps unless new ones are entered. */
@Component({
  selector: 'app-channel-form',
  imports: [FormField],
  templateUrl: './channel-form.html',
  styleUrl: './channel-form.scss',
})
export class ChannelForm {
  private readonly api = inject(NotificationsApi);

  /** The channel to edit; `null` to set up a new one. */
  readonly channel = input<NotificationChannelSummary | null>(null);
  readonly saved = output<NotificationChannelSummary>();
  readonly cancelled = output();

  protected readonly editing = computed(() => this.channel() !== null);
  /** Tells this form's title apart from another's on the page. */
  protected readonly formId = computed(() => `channel-form-${this.channel()?.id ?? 'new'}`);
  protected readonly error = signal<string | null>(null);

  private readonly model = linkedSignal(() => toModel(this.channel()));
  protected readonly channelForm = form(this.model, (path) => {
    required(path.name, { message: 'Give the channel a name.' });
    required(path.applicationToken, {
      message: "Enter the Pushover application's API token.",
      when: () => !this.editing(),
    });
    required(path.userKey, { message: 'Enter your Pushover user key, or a group key.', when: () => !this.editing() });
    required(path.retrySeconds, { message: 'Enter how often an emergency repeats.' });
    min(path.retrySeconds, minRetrySeconds, { message: `Pushover repeats at most every ${minRetrySeconds} seconds.` });
    max(path.retrySeconds, maxExpireSeconds, { message: 'Repeat at least once every 3 hours.' });
    required(path.expireSeconds, { message: 'Enter how long an emergency keeps repeating.' });
    min(path.expireSeconds, minRetrySeconds, { message: `Keep repeating for at least ${minRetrySeconds} seconds.` });
    max(path.expireSeconds, maxExpireSeconds, { message: 'Pushover stops repeating after 3 hours (10800 seconds).' });
  });

  protected save(event: Event) {
    event.preventDefault();
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.channelForm, async () => {
      this.error.set(null);
      const dto = toDto(this.model());
      const channel = this.channel();
      try {
        const saved = channel ? await this.api.updateChannel(channel.id, dto) : await this.api.createChannel(dto);
        this.saved.emit(saved);
      } catch (e) {
        this.error.set(describeSaveError(e));
      }
      return undefined;
    });
  }
}

/** ASP.NET's validation problems come back as a map of field to messages; this flattens them into sentences. */
function describeSaveError(error: unknown): string {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object' && 'errors' in error.error) {
    const errors = (error.error as { errors: Record<string, string[]> }).errors;
    return Object.values(errors).flat().join('\n');
  }
  return describeError(error);
}
