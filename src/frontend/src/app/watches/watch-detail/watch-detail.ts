import { DatePipe } from '@angular/common';
import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { FormField, form, submit } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { OutcomeBadge } from '../outcome-badge/outcome-badge';
import { problemMessage } from '../../shared/problem';
import { refresh } from '../../shared/refresh';
import { RunTiming } from '../run-timing/run-timing';
import { ScheduleEditor } from '../../schedules/schedule-editor/schedule-editor';
import { StatusBadge } from '../status-badge/status-badge';
import { type Run, type Watch, type WatchSettings, outcomeLabels } from '../watch.model';
import { defaultTimeoutSeconds, maxTimeoutSeconds, nameAndNotificationUrlRules, timeoutRules } from '../watch-settings';
import { Watches } from '../watches';

const historyPageSize = 50;

type WatchAction = 'running' | 'pausing' | 'resuming' | 'deleting';

/** The pages of Run history loaded beyond the latest page. Each change makes a new object, so a stale load can tell. */
interface OlderRuns {
  runs: Run[];
  ended: boolean;
  loading: boolean;
}

const noOlderRuns: OlderRuns = { runs: [], ended: false, loading: false };

@Component({
  selector: 'app-watch-detail',
  imports: [DatePipe, FormField, OutcomeBadge, RouterLink, RunTiming, ScheduleEditor, StatusBadge],
  templateUrl: './watch-detail.html',
  styleUrl: './watch-detail.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchDetail {
  private readonly watches = inject(Watches);
  private readonly router = inject(Router);
  readonly id = input.required<string>();

  // Each read's request is a new object, so bumping its counter restarts a read that reload() won't.
  private readonly watchReads = signal(0);
  private readonly runReads = signal(0);
  protected readonly watch = httpResource<Watch>(() => {
    this.watchReads();
    return { url: `/api/watches/${this.id()}` };
  });
  protected readonly latestRuns = httpResource<Run[]>(() => {
    this.runReads();
    return { url: `/api/watches/${this.id()}/runs`, params: { limit: historyPageSize } };
  });
  // A resource's value() throws while it is in an error state, so this checks hasValue() first.
  private readonly latestPage = computed(() => (this.latestRuns.hasValue() ? this.latestRuns.value() : undefined));
  // Older pages continue the latest page they were loaded after, so they reset whenever it or the Watch changes.
  // Moving to another Watch's page reuses this component, which is why the other per-Watch state below resets too.
  protected readonly olderRuns = linkedSignal<unknown, OlderRuns>({
    source: () => [this.id(), this.latestPage()],
    computation: () => noOlderRuns,
  });
  protected readonly runs = computed(() => [...(this.latestPage() ?? []), ...this.olderRuns().runs]);
  protected readonly canShowOlderRuns = computed(() => !this.olderRuns().ended
    && this.latestPage()?.length === historyPageSize);
  protected readonly missing = computed(() => {
    const error = this.watch.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });

  private readonly savedSettings = computed<WatchSettings | undefined>(() => {
    if (!this.watch.hasValue()) {return undefined;}
    const watch = this.watch.value();
    return { name: watch.name, notificationUrl: watch.notificationUrl, timeoutSeconds: watch.timeoutSeconds };
  }, { equal: sameSettings });
  // The settings this page is saving or last saved, whose arrival must not discard edits typed meanwhile.
  private submitted: { id: string; settings: WatchSettings } | undefined;
  // Follows the saved settings, so pausing or running the Watch doesn't discard unsaved edits.
  protected readonly model = linkedSignal<WatchSettings | undefined, WatchSettings>({
    source: this.savedSettings,
    computation: (saved, previous) => {
      const submitted = this.submitted;
      // Settings shown before (not the placeholder while loading) carry the edits worth keeping.
      if (previous?.source && saved && submitted?.id === untracked(this.id) && sameSettings(saved, submitted.settings)) {
        return previous.value;
      }
      return saved ?? { name: '', notificationUrl: '', timeoutSeconds: defaultTimeoutSeconds };
    },
  });
  protected readonly settingsForm = form(this.model, (fields) => {
    nameAndNotificationUrlRules(fields);
    timeoutRules(fields.timeoutSeconds);
  });
  protected readonly maxTimeoutSeconds = maxTimeoutSeconds;

  protected readonly busy = linkedSignal<string, WatchAction | undefined>({ source: this.id, computation: () => undefined });
  protected readonly confirmingDelete = linkedSignal({ source: this.id, computation: () => false });
  protected readonly editingSchedule = linkedSignal({ source: this.id, computation: () => false });
  protected readonly error = linkedSignal({ source: this.id, computation: () => '' });
  protected readonly notice = linkedSignal({ source: this.id, computation: () => '' });
  protected readonly outcomes = outcomeLabels;

  protected async save() {
    const id = this.id();
    await submit(this.settingsForm, async () => {
      this.clearMessages();
      const settings = this.model();
      this.submitted = { id, settings };
      try {
        await this.watches.edit(id, settings);
        if (id !== this.id()) {return;}
        this.notice.set('Settings saved.');
      } catch (error: unknown) {
        this.submitted = undefined;
        if (id === this.id()) {
          this.error.set(problemMessage(error, 'Could not save the settings. Check the fields and try again.'));
        }
      }
      this.refreshWatch();
    });
  }

  protected scheduleSaved() {
    this.editingSchedule.set(false);
    this.clearMessages();
    this.notice.set('Schedule saved.');
    // The editor has already saved; read the Watch afresh rather than trusting its response.
    this.refreshWatch();
  }

  protected async runNow() {
    await this.performAction('running', 'Could not run the Watch. Please try again.', async (isCurrent) => {
      try {
        const run = await this.watches.runNow(this.id());
        if (isCurrent()) {
          this.notice.set(`Run finished: ${this.outcomes[run.outcome]}.${run.isChange ? ' Change recorded.' : ''}`);
        }
      } finally {
        // A rejected Run can mean the Watch changed or was deleted elsewhere, so refresh either way.
        if (isCurrent()) {
          refresh(this.latestRuns, this.runReads);
        }
      }
    });
  }

  protected async togglePaused({ isPaused }: Watch) {
    const action = isPaused ? 'resuming' : 'pausing';
    await this.performAction(action, `Could not ${isPaused ? 'resume' : 'pause'} the Watch. Please try again.`, async (isCurrent) => {
      const id = this.id();
      await (isPaused ? this.watches.resume(id) : this.watches.pause(id));
      if (isCurrent()) {
        this.notice.set(isPaused ? 'Watch resumed.' : 'Watch paused. It will not run on its schedule.');
      }
    });
  }

  protected async delete() {
    await this.performAction('deleting', 'Could not delete the Watch. Please try again.', async () => {
      await this.watches.delete(this.id());
      await this.router.navigateByUrl('/');
    });
  }

  protected async showOlderRuns() {
    const current = this.olderRuns();
    const oldest = this.runs().at(-1);
    if (current.loading || current.ended || this.latestRuns.isLoading() || !oldest) {return;}
    const loading = { ...current, loading: true };
    this.olderRuns.set(loading);
    try {
      const page = await this.watches.history(this.id(), oldest.id, historyPageSize);
      // A refreshed latest page or another Watch replaced the pages this one would extend.
      if (this.olderRuns() !== loading) {return;}
      this.olderRuns.set({ runs: [...current.runs, ...page], ended: page.length < historyPageSize, loading: false });
    } catch (error: unknown) {
      if (this.olderRuns() !== loading) {return;}
      this.olderRuns.set(current);
      this.error.set(problemMessage(error, 'Could not load older Runs. Please try again.'));
    }
  }

  /**
   * Runs one of the Watch's actions, showing its failure message if it fails, then reads the Watch afresh rather than
   * trusting any response, which a later change may already have superseded.
   */
  private async performAction(action: WatchAction, failure: string,
    work: (isCurrent: () => boolean) => Promise<void>) {
    const id = this.id();
    const isCurrent = () => id === this.id();
    this.busy.set(action);
    this.clearMessages();
    try {
      await work(isCurrent);
    } catch (error: unknown) {
      if (isCurrent()) {this.error.set(problemMessage(error, failure));}
    } finally {
      if (isCurrent()) {
        this.busy.set(undefined);
        this.refreshWatch();
      }
    }
  }

  private refreshWatch() {
    refresh(this.watch, this.watchReads);
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
