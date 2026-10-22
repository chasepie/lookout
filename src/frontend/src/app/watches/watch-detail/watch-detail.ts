import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, linkedSignal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormField, form, submit } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { OutcomeBadge } from '../outcome-badge/outcome-badge';
import { problemMessage } from '../../shared/problem';
import { refreshableHttpResource } from '../../shared/refreshable-http-resource';
import { RunDebugOutput } from '../run-debug-output/run-debug-output';
import { RunTiming } from '../run-timing/run-timing';
import { ScheduleEditor } from '../../schedules/schedule-editor/schedule-editor';
import { StatusBadge } from '../status-badge/status-badge';
import { WatchEvents } from '../watch-events';
import { type Run, type ScriptVersion, type Watch, type WatchSettings, outcomeLabels } from '../watch.model';
import { defaultTimeoutSeconds, maxTimeoutSeconds, nameAndNotificationUrlRules, timeoutRules } from '../watch-settings';
import { WatchChannels } from '../watch-channels/watch-channels';
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
  imports: [DatePipe, FormField, OutcomeBadge, RouterLink, RunDebugOutput, RunTiming, ScheduleEditor, StatusBadge, WatchChannels],
  templateUrl: './watch-detail.html',
  styleUrl: './watch-detail.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchDetail {
  private readonly watches = inject(Watches);
  private readonly router = inject(Router);
  readonly id = input.required<string>();

  protected readonly watch = refreshableHttpResource<Watch>(() => ({ url: `/api/watches/${this.id()}` }));
  protected readonly latestRuns = refreshableHttpResource<Run[]>(() => ({
    url: `/api/watches/${this.id()}/runs`,
    params: { limit: historyPageSize },
  }));
  protected readonly scriptVersions = refreshableHttpResource<ScriptVersion[]>(() => ({
    url: `/api/watches/${this.id()}/script-versions`,
  }));
  /** Each Script version's number by its ID, to show which version each Run ran. */
  protected readonly versionNumbers = computed(() => new Map(
    (this.scriptVersions.hasValue() ? this.scriptVersions.value() : []).map((version) => [version.id, version.number]),
  ));
  /** The source of each Script version on show, or null while it loads. A version's source never changes. */
  protected readonly shownSources = linkedSignal<string, ReadonlyMap<string, string | null>>({
    source: this.id,
    computation: () => new Map(),
  });
  // An action that finishes after leaving this page must not act on whatever page is shown by then.
  private destroyed = false;
  // A resource's value() throws while it is in an error state, so this checks hasValue() first.
  private readonly latestPage = computed(() => (this.latestRuns.hasValue() ? this.latestRuns.value() : undefined));
  // Older pages continue the latest page they were loaded after. When it's read again, such as after a Run, the older
  // pages shown are kept below it as long as it still reaches them, so nothing is skipped; otherwise, and when the
  // Watch changes, they reset. An older page still loading is discarded either way.
  // Moving to another Watch's page reuses this component, which is why the other per-Watch state below resets too.
  protected readonly olderRuns = linkedSignal<{ id: string; latest: Run[] | undefined }, OlderRuns>({
    source: () => ({ id: this.id(), latest: this.latestPage() }),
    computation: ({ id, latest }, previous) => {
      const older = previous?.value.runs ?? [];
      const oldest = latest?.at(-1)?.id;
      if (previous?.source.id !== id || !older.length || oldest === undefined) {return noOlderRuns;}
      const shown = [...(previous.source.latest ?? []), ...older];
      if (!shown.some((run) => run.id === oldest)) {return noOlderRuns;}
      return { runs: shown.filter((run) => run.id < oldest), ended: previous.value.ended, loading: false };
    },
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
  // The settings this page is saving, as the backend will store them, until they arrive back. Their arrival must not
  // discard edits typed meanwhile.
  private submitted: { id: string; settings: WatchSettings } | undefined;
  // Follows the saved settings, so pausing or running the Watch doesn't discard unsaved edits.
  protected readonly model = linkedSignal<WatchSettings | undefined, WatchSettings>({
    source: this.savedSettings,
    computation: (saved, previous) => {
      const submitted = this.submitted;
      // Settings shown before (not the placeholder while loading) carry the edits worth keeping.
      if (previous?.source && saved && submitted?.id === untracked(this.id) && sameSettings(saved, submitted.settings)) {
        // Consumed once, so a later change elsewhere back to these same settings is shown.
        this.submitted = undefined;
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

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
    });
    // Read afresh rather than patched, as an event may overtake a read already under way.
    inject(WatchEvents).events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event.type !== 'connected' && event.watchId !== this.id()) {return;}
      this.refreshWatch();
      if (event.type === 'connected' || (event.type === 'runFinished' && event.runId !== null)) {
        this.latestRuns.refresh();
      }
    });
  }

  protected async save() {
    const id = this.id();
    await submit(this.settingsForm, async () => {
      this.clearMessages();
      const settings = this.model();
      this.submitted = { id, settings: trimmed(settings) };
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

  protected channelsSaved() {
    this.clearMessages();
    this.notice.set('Channels saved.');
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
          this.latestRuns.refresh();
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
    await this.performAction('deleting', 'Could not delete the Watch. Please try again.', async (isCurrent) => {
      await this.watches.delete(this.id());
      if (isCurrent()) {
        await this.router.navigateByUrl('/');
      }
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

  protected async toggleSource({ id: versionId }: ScriptVersion) {
    const id = this.id();
    if (this.shownSources().has(versionId)) {
      this.shownSources.update((sources) => without(sources, versionId));
      return;
    }
    this.shownSources.update((sources) => new Map(sources).set(versionId, null));
    // Hidden again or moved to another Watch meanwhile: there is nowhere to show it.
    const isWanted = () => !this.destroyed && id === this.id() && this.shownSources().has(versionId);
    try {
      const { source } = await this.watches.scriptVersion(id, versionId);
      if (isWanted()) {this.shownSources.update((sources) => new Map(sources).set(versionId, source));}
    } catch (error: unknown) {
      if (isWanted()) {
        this.shownSources.update((sources) => without(sources, versionId));
        this.error.set(problemMessage(error, 'Could not load the Script source. Please try again.'));
      }
    }
  }

  /**
   * Runs one of the Watch's actions, showing its failure message if it fails, then reads the Watch afresh rather than
   * trusting any response, which a later change may already have superseded.
   */
  private async performAction(action: WatchAction, failure: string,
    work: (isCurrent: () => boolean) => Promise<void>) {
    const id = this.id();
    const isCurrent = () => !this.destroyed && id === this.id();
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
    this.watch.refresh();
    // A version uploaded elsewhere changes the current one, and Runs of it need its number.
    this.scriptVersions.refresh();
  }

  private clearMessages() {
    this.error.set('');
    this.notice.set('');
  }
}

function without<K, V>(map: ReadonlyMap<K, V>, key: K): ReadonlyMap<K, V> {
  const copy = new Map(map);
  copy.delete(key);
  return copy;
}

/** The settings as the backend stores them, which trims the name and Notification URL. */
function trimmed(settings: WatchSettings): WatchSettings {
  return { ...settings, name: settings.name.trim(), notificationUrl: settings.notificationUrl.trim() };
}

function sameSettings(a: WatchSettings | undefined, b: WatchSettings | undefined): boolean {
  return a === b || (!!a && !!b && a.name === b.name && a.notificationUrl === b.notificationUrl
    && a.timeoutSeconds === b.timeoutSeconds);
}
