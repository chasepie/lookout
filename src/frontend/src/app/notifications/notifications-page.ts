import { Component, inject, signal } from '@angular/core';
import { reloader } from '../live/reload-on';
import { describeError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
import { Notices } from '../shared/notices';
import { ChannelCard } from './channel-card';
import { ChannelForm } from './channel-form';
import { NotificationChannelSummary } from './notification.model';
import { NotificationsApi } from './notifications-api';

/** The notification channels the Owner has set up, and setting up more. */
@Component({
  selector: 'app-notifications-page',
  imports: [ChannelCard, ChannelForm],
  template: `
    <h1>Notifications</h1>
    <p class="intro muted">
      Where notifications about watches go. Each watch chooses which of its events to send to which channel.
    </p>

    @if (shownChannels(); as list) {
      @if (channels.error(); as error) {
        <div class="callout callout-failure callout-retry" role="alert">
          <p>Couldn't refresh the channels: {{ describeError(error) }}</p>
          <button type="button" class="button" (click)="channels.reload()">Try again</button>
        </div>
      }
      @if (adding()) {
        <app-channel-form (saved)="added($event)" (cancelled)="adding.set(false)" />
      } @else {
        <div class="actions">
          <button type="button" class="button button-primary" (click)="adding.set(true)">Add a channel</button>
        </div>
      }
      @if (list.length === 0 && !adding()) {
        <p class="muted">No channels yet. Add one, then choose on each watch what it sends there.</p>
      }
      <ul class="channels">
        @for (channel of list; track channel.id) {
          <li>
            @if (editingId() === channel.id) {
              <app-channel-form [channel]="channel" (saved)="edited($event)" (cancelled)="editingId.set(null)" />
            } @else {
              <app-channel-card
                [channel]="channel"
                (edit)="editingId.set(channel.id)"
                (deleted)="reloadChannels()"
              />
            }
          </li>
        }
      </ul>
    } @else if (channels.error(); as error) {
      <div class="callout callout-failure callout-retry" role="alert">
        <p>Couldn't load the channels: {{ describeError(error) }}</p>
        <button type="button" class="button" (click)="channels.reload()">Try again</button>
      </div>
    } @else {
      <p class="muted" role="status">Loading the channels…</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-5);
      max-width: 48rem;
    }

    .channels {
      display: grid;
      gap: var(--space-4);
      margin: 0;
      padding: 0;
      list-style: none;
    }
  `,
})
export class NotificationsPage {
  private readonly api = inject(NotificationsApi);

  protected readonly channels = this.api.channelsResource();
  /** The channels as last loaded, still shown if a refresh fails. */
  protected readonly shownChannels = lastValue(this.channels);
  /** Reloads the channels after a change, again once a load already under way ends, which may have missed it. */
  protected readonly reloadChannels = reloader(this.channels);

  protected readonly describeError = describeError;
  private readonly notices = inject(Notices);
  protected readonly adding = signal(false);
  /** The channel being edited, which shows its form in place of its card. */
  protected readonly editingId = signal<number | null>(null);

  protected added(channel: NotificationChannelSummary) {
    this.adding.set(false);
    this.notices.say(`Added ${channel.name}.`);
    this.reloadChannels();
  }

  protected edited(channel: NotificationChannelSummary) {
    this.editingId.set(null);
    this.notices.say(`Saved ${channel.name}.`);
    this.reloadChannels();
  }
}
