import { Component, computed, inject, input, output, signal } from '@angular/core';
import { Confirm } from '../shared/confirm';
import { describeError } from '../shared/describe-error';
import { Notices } from '../shared/notices';
import { describeSeconds } from './durations';
import { NotificationChannelSummary, NotificationService } from './notification.model';
import { NotificationsApi } from './notifications-api';

const serviceNames: Record<NotificationService, string> = {
  [NotificationService.Pushover]: 'Pushover',
};

const listFormat = new Intl.ListFormat('en', { type: 'conjunction' });

/** One notification channel: what it is and which watches use it. */
@Component({
  selector: 'app-channel-card',
  template: `
    <article class="channel" [attr.aria-labelledby]="'channel-' + channel().id">
      <header class="head">
        <h2 [id]="'channel-' + channel().id">{{ channel().name }}</h2>
        <span class="service">{{ serviceName() }}</span>
      </header>
      <p class="facts">
        @if (channel().watches.length === 0) {
          No watch uses it yet.
        } @else {
          Used by {{ watchNames() }}.
        }
      </p>
      @if (channel().pushover; as pushover) {
        <p class="muted">
          Emergencies repeat every {{ describeSeconds(pushover.retrySeconds) }} for
          {{ describeSeconds(pushover.expireSeconds) }}.
        </p>
      }
      <div class="actions">
        <button type="button" class="button" [disabled]="testing()" (click)="sendTest()">Send a test</button>
        <button type="button" class="button" (click)="edit.emit()">Edit</button>
        <button type="button" class="button button-quiet" (click)="remove()">Delete</button>
      </div>
      <div role="status">
        @if (testSent()) {
          <p class="muted">Sent a test notification. Check that it arrived.</p>
        }
      </div>
      @if (testError(); as error) {
        <div class="callout callout-failure" role="alert">
          <p>Couldn't send a test: {{ error }}</p>
        </div>
      }
    </article>
  `,
  styles: `
    .channel {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-4) var(--space-5);
      border-left: 3px solid var(--deep);
      background: var(--paper-raised);
    }

    .head {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-3);
    }

    h2 {
      margin: 0;
      font-size: var(--step-1);
    }

    .service {
      font-family: var(--font-condensed);
      font-size: var(--step--1);
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }

    p {
      margin: 0;
    }
  `,
})
export class ChannelCard {
  readonly channel = input.required<NotificationChannelSummary>();
  readonly edit = output();
  readonly deleted = output();

  private readonly api = inject(NotificationsApi);
  private readonly confirm = inject(Confirm);
  private readonly notices = inject(Notices);
  protected readonly testing = signal(false);
  protected readonly testSent = signal(false);
  protected readonly testError = signal<string | null>(null);

  protected readonly serviceName = computed(() => serviceNames[this.channel().service]);
  protected readonly watchNames = computed(() => listFormat.format(this.channel().watches.map((w) => w.name)));
  protected readonly describeSeconds = describeSeconds;

  protected async remove() {
    const { id, name, watches } = this.channel();
    const sure = await this.confirm.ask({
      title: `Delete ${name}?`,
      body:
        watches.length === 0
          ? 'No watch uses it.'
          : `${this.watchNames()} stop sending notifications to it.`,
      confirmLabel: 'Delete channel',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    try {
      await this.api.deleteChannel(id);
      this.notices.say(`Deleted ${name}.`);
      this.deleted.emit();
    } catch (e) {
      this.notices.fail(`Couldn't delete ${name}: ${describeError(e)}`);
    }
  }

  protected async sendTest() {
    this.testing.set(true);
    this.testSent.set(false);
    this.testError.set(null);
    try {
      await this.api.sendTest(this.channel().id);
      this.testSent.set(true);
    } catch (e) {
      this.testError.set(describeProblem(e));
    } finally {
      this.testing.set(false);
    }
  }
}

/** A problem's detail, which says why the service refused, or else the error as `describeError` reads it. */
function describeProblem(error: unknown): string {
  if (error && typeof error === 'object' && 'error' in error) {
    const body = error.error;
    if (body && typeof body === 'object' && 'detail' in body && typeof body.detail === 'string') {
      return body.detail;
    }
  }
  return describeError(error);
}
