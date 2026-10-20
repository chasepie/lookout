import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormField, form, submit } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { problemMessage } from './problem';
import { type Run, type Watch, type WatchSettings, outcomeLabels, statusLabels } from './watch.model';
import { defaultTimeoutSeconds, maxTimeoutSeconds, nameAndNotificationUrlRules, timeoutRules } from './watch-settings';
import { Watches } from './watches';

const historyPageSize = 50;

type WatchAction = 'running' | 'pausing' | 'resuming' | 'deleting';

@Component({
  selector: 'app-watch-detail',
  imports: [DatePipe, DecimalPipe, FormField, RouterLink],
  templateUrl: './watch-detail.html',
  styleUrl: './watch-detail.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchDetail {
  private readonly watches = inject(Watches);
  private readonly router = inject(Router);
  readonly id = input.required<string>();

  protected readonly watch = httpResource<Watch>(() => `/api/watches/${this.id()}`);
  protected readonly latestRuns = httpResource<Run[]>(() => ({
    url: `/api/watches/${this.id()}/runs`, params: { limit: historyPageSize },
  }));
  protected readonly olderRuns = signal<Run[]>([]);
  private readonly olderRunsEnded = signal(false);
  // A resource's value() throws while it is in an error state, so these check hasValue() first.
  protected readonly runs = computed(() => [
    ...(this.latestRuns.hasValue() ? this.latestRuns.value() : []), ...this.olderRuns()]);
  protected readonly canShowOlderRuns = computed(() => !this.olderRunsEnded()
    && this.latestRuns.hasValue() && this.latestRuns.value().length === historyPageSize);
  protected readonly missing = computed(() => {
    const error = this.watch.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });

  private readonly savedSettings = computed<WatchSettings | undefined>(() => {
    if (!this.watch.hasValue()) {return undefined;}
    const watch = this.watch.value();
    return { name: watch.name, notificationUrl: watch.notificationUrl, timeoutSeconds: watch.timeoutSeconds };
  }, { equal: sameSettings });
  // Follows the saved settings, so pausing or running the Watch doesn't discard unsaved edits.
  protected readonly model = linkedSignal<WatchSettings>(
    () => this.savedSettings() ?? { name: '', notificationUrl: '', timeoutSeconds: defaultTimeoutSeconds });
  protected readonly settingsForm = form(this.model, (fields) => {
    nameAndNotificationUrlRules(fields);
    timeoutRules(fields.timeoutSeconds);
  });
  protected readonly maxTimeoutSeconds = maxTimeoutSeconds;

  protected readonly busy = signal<WatchAction | undefined>(undefined);
  protected readonly confirmingDelete = signal(false);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly outcomes = outcomeLabels;
  protected readonly statuses = statusLabels;

  protected async save() {
    await submit(this.settingsForm, async () => {
      this.clearMessages();
      try {
        this.watch.set(await this.watches.edit(this.id(), this.model()));
        this.notice.set('Settings saved.');
        this.watches.list.reload();
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not save the settings. Check the fields and try again.'));
      }
    });
  }

  protected async runNow() {
    await this.performAction('running', 'Could not run the Watch. Please try again.', async () => {
      try {
        const run = await this.watches.runNow(this.id());
        this.notice.set(`Run finished: ${this.outcomes[run.outcome]}.${run.isChange ? ' Change recorded.' : ''}`);
      } finally {
        // A rejected Run can mean the Watch changed or was deleted elsewhere, so refresh either way.
        this.olderRuns.set([]);
        this.olderRunsEnded.set(false);
        this.latestRuns.reload();
        this.watch.reload();
      }
    });
  }

  protected async togglePaused({ isPaused }: Watch) {
    await this.performAction(isPaused ? 'resuming' : 'pausing', `Could not ${isPaused ? 'resume' : 'pause'} the Watch. Please try again.`, async () => {
      const id = this.id();
      this.watch.set(await (isPaused ? this.watches.resume(id) : this.watches.pause(id)));
      this.notice.set(isPaused ? 'Watch resumed.' : 'Watch paused. It will not run on its schedule.');
    });
  }

  protected async delete() {
    await this.performAction('deleting', 'Could not delete the Watch. Please try again.', async () => {
      await this.watches.delete(this.id());
      await this.router.navigateByUrl('/');
    });
  }

  protected async showOlderRuns() {
    const oldest = this.runs().at(-1);
    if (!oldest) {return;}
    try {
      const page = await this.watches.history(this.id(), oldest.id, historyPageSize);
      this.olderRuns.update((runs) => [...runs, ...page]);
      this.olderRunsEnded.set(page.length < historyPageSize);
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not load older Runs. Please try again.'));
    }
  }

  /** Runs one of the Watch's actions, showing its failure message if it fails */
  private async performAction(action: WatchAction, failure: string, work: () => Promise<void>) {
    this.busy.set(action);
    this.clearMessages();
    try {
      await work();
      this.watches.list.reload();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, failure));
    } finally {
      this.busy.set(undefined);
    }
  }

  private clearMessages() {
    this.error.set('');
    this.notice.set('');
  }
}

function sameSettings(a: WatchSettings | undefined, b: WatchSettings | undefined): boolean {
  return a === b || (!!a && !!b && a.name === b.name && a.notificationUrl === b.notificationUrl
    && a.timeoutSeconds === b.timeoutSeconds);
}
