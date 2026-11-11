import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, linkedSignal, signal } from '@angular/core';
import { FormField, form, max, min, required, submit } from '@angular/forms/signals';
import { Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import { NotificationsApi } from '../notifications/notifications-api';
import {
  SubscriptionModel,
  subscriptionProblems,
  toSubscriptionDtos,
  toSubscriptionModels,
} from '../notifications/subscriptions';
import { SubscriptionsEditor } from '../notifications/subscriptions-editor';
import { describeError, describeSaveError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
import { Notices } from '../shared/notices';
import { ScriptPicker } from './script-picker';
import { describeCron, intervalUnits } from './schedule';
import { CreateWatchDto, Watch, WatchScheduleKind, WatchSummary } from './watch.model';
import { WatchesApi } from './watches-api';

interface WatchFormModel {
  name: string;
  scheduleEnabled: boolean;
  scheduleType: 'interval' | 'cron';
  every: number;
  /** An interval kind's numeric value, as a string for the select. */
  unit: string;
  cron: string;
  useVpn: boolean;
  screenshotOnSuccess: boolean;
  screenshotOnKnownFailure: boolean;
  screenshotOnCrash: boolean;
  knownFailureThreshold: number;
}

/** The most known failures in a row a watch can wait for, as the Backend allows. */
const maxKnownFailureThreshold = 1000;

const units = Object.entries(intervalUnits).map(([value, [, many]]) => ({ value, label: many }));

const blank: WatchFormModel = {
  name: '',
  scheduleEnabled: false,
  scheduleType: 'interval',
  every: 15,
  unit: String(WatchScheduleKind.Minutely),
  cron: '',
  useVpn: false,
  screenshotOnSuccess: false,
  screenshotOnKnownFailure: false,
  screenshotOnCrash: false,
  knownFailureThreshold: 3,
};

/** The form's view of a saved watch. A schedule that's off keeps its settings, so turning it on again restores them. */
function toModel(watch: WatchSummary): WatchFormModel {
  const cron = watch.scheduleKind === WatchScheduleKind.Cron;
  const interval = watch.scheduleKind !== null && !cron;
  return {
    name: watch.name,
    scheduleEnabled: watch.scheduleEnabled,
    scheduleType: cron ? 'cron' : 'interval',
    every: interval ? Number(watch.scheduleInterval) || blank.every : blank.every,
    unit: interval ? String(watch.scheduleKind) : blank.unit,
    cron: cron ? (watch.scheduleInterval ?? '') : '',
    useVpn: watch.useVpn,
    screenshotOnSuccess: watch.screenshotOnSuccess,
    screenshotOnKnownFailure: watch.screenshotOnKnownFailure,
    screenshotOnCrash: watch.screenshotOnCrash,
    knownFailureThreshold: watch.knownFailureThreshold,
  };
}

/**
 * The watch to save. A schedule that's off keeps its settings for when it's turned on again, but only valid ones:
 * the form checks them only while the schedule is on.
 */
function toDto(value: WatchFormModel): CreateWatchDto {
  const cron = value.scheduleType === 'cron';
  const interval = cron
    ? value.cron.trim() || null
    : Number.isInteger(value.every) && value.every >= 1
      ? String(value.every)
      : null;
  return {
    name: value.name.trim(),
    scheduleEnabled: value.scheduleEnabled,
    scheduleKind: interval === null ? null : cron ? WatchScheduleKind.Cron : Number(value.unit),
    scheduleInterval: interval,
    useVpn: value.useVpn,
    screenshotOnSuccess: value.screenshotOnSuccess,
    screenshotOnKnownFailure: value.screenshotOnKnownFailure,
    screenshotOnCrash: value.screenshotOnCrash,
    knownFailureThreshold: value.knownFailureThreshold,
  };
}

/** Creates a watch, or edits one when the route has an `id`. */
@Component({
  selector: 'app-watch-form',
  imports: [FormField, RouterLink, ScriptPicker, SubscriptionsEditor],
  templateUrl: './watch-form.html',
  styleUrl: './watch-form.scss',
})
export class WatchForm {
  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);
  private readonly notices = inject(Notices);
  private readonly notifications = inject(NotificationsApi);

  /** Route param; absent when creating. */
  readonly id = input<string>();

  protected readonly units = units;
  protected readonly describeError = describeError;
  protected readonly error = signal<string | null>(null);
  /** The script to create the watch with, or to replace its script with when editing. */
  protected readonly script = signal<File | null>(null);
  /** Shown once the Owner tries to create a watch without one. */
  protected readonly scriptMissing = signal(false);

  protected readonly existing = this.api.watchResource(this.id);
  protected readonly editing = computed(() => this.id() !== undefined);

  /** Starts blank, or as the saved watch once it loads when editing; then holds the Owner's edits. */
  private readonly model = linkedSignal<WatchFormModel>(() =>
    this.editing() && this.existing.hasValue() ? toModel(this.existing.value()) : { ...blank },
  );
  protected readonly watchForm = form(this.model, (path) => {
    required(path.name, { message: 'Give the watch a name.' });
    required(path.every, {
      message: 'Enter how often to run.',
      when: ({ valueOf }) => valueOf(path.scheduleEnabled) && valueOf(path.scheduleType) === 'interval',
    });
    min(path.every, 1, {
      message: 'Enter 1 or more.',
      when: ({ valueOf }) => valueOf(path.scheduleEnabled) && valueOf(path.scheduleType) === 'interval',
    });
    required(path.knownFailureThreshold, { message: 'Enter how many known failures in a row to notify after.' });
    min(path.knownFailureThreshold, 1, { message: 'Enter 1 or more.' });
    max(path.knownFailureThreshold, maxKnownFailureThreshold, {
      message: `Enter ${maxKnownFailureThreshold} or fewer.`,
    });
    required(path.cron, {
      message: 'Enter a cron expression, such as 0 9 * * 1-5.',
      when: ({ valueOf }) => valueOf(path.scheduleEnabled) && valueOf(path.scheduleType) === 'cron',
    });
  });

  /** The cron expression in plain words as the Owner types it: empty while it's blank, null if it can't be read. */
  protected readonly cronDescription = computed(() => {
    const cron = this.watchForm.cron().value();
    return cron.trim() ? describeCron(cron) : '';
  });

  protected readonly channels = this.notifications.channelsResource();
  /** The channels as last loaded, still offered if a refresh fails. */
  protected readonly shownChannels = lastValue(this.channels);
  protected readonly savedSubscriptions = this.notifications.subscriptionsResource(this.id);
  /** The saved subscriptions once they load, or none for a new watch; then the Owner's edits. */
  protected readonly subscriptions = linkedSignal<SubscriptionModel[]>(() =>
    this.savedSubscriptions.hasValue() ? toSubscriptionModels(this.savedSubscriptions.value()) : [],
  );
  /** Whether to say what's wrong with the subscriptions, once the Owner has tried to save. */
  protected readonly showSubscriptionProblems = signal(false);
  /** Whether the subscriptions differ from the saved ones, so saving the watch saves them too. */
  private readonly subscriptionsChanged = computed(() => {
    const saved = this.savedSubscriptions.hasValue() ? this.savedSubscriptions.value() : [];
    return JSON.stringify(toSubscriptionDtos(this.subscriptions())) !== JSON.stringify(saved);
  });

  /** Whether there's no such watch to edit. */
  protected readonly missing = computed(() => {
    const error = this.existing.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  /** Whether the form can be shown: always when creating, and once the watch has loaded when editing. */
  protected readonly ready = computed(
    () =>
      !this.editing() ||
      (this.existing.hasValue() && (this.savedSubscriptions.hasValue() || !!this.savedSubscriptions.error())),
  );
  protected readonly cancelLink = computed(() => (this.editing() ? ['/watches', this.id()] : ['/']));

  constructor() {
    const title = inject(Title);
    effect(() => {
      if (this.editing() && this.existing.hasValue()) {
        title.setTitle(`Edit ${this.existing.value().name} – Lookout`);
      }
    });
  }

  protected save(event: Event) {
    event.preventDefault();
    const script = this.script();
    if (!this.editing() && !script) {
      this.scriptMissing.set(true);
    }
    this.showSubscriptionProblems.set(true);
    const subscriptionsValid = subscriptionProblems(this.subscriptions(), this.shownChannels() ?? []).every(
      (p) => p === null,
    );
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.watchForm, async () => {
      if (!subscriptionsValid) {
        return undefined;
      }
      this.error.set(null);
      const dto = toDto(this.model());
      try {
        const id = this.id();
        let saved: Watch;
        if (id) {
          saved = await this.api.update(id, dto);
          if (script) {
            await this.api.uploadScript(id, script);
          }
        } else if (script) {
          saved = await this.api.create(dto, script);
        } else {
          return undefined;
        }
        if (this.subscriptionsChanged() && !this.savedSubscriptions.error()) {
          try {
            await this.notifications.replaceSubscriptions(saved.id, toSubscriptionDtos(this.subscriptions()));
          } catch (e) {
            // The watch is saved, so it isn't saved again: a new one would be created twice
            this.notices.fail(`Saved ${saved.name}, but couldn't save its notifications: ${describeSaveError(e)}`);
            await this.router.navigate(['/watches', saved.id]);
            return undefined;
          }
        }
        this.notices.say(id ? `Saved ${saved.name}.` : `Created ${saved.name}.`);
        await this.router.navigate(['/watches', saved.id]);
      } catch (e) {
        this.error.set(describeSaveError(e));
      }
      return undefined;
    });
  }
}
