import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { LucideChevronLeft, LucideEllipsis, LucideEraser, LucidePencil, LucidePlay, LucideTrash2 } from '@lucide/angular';
import { filter } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn } from '../live/reload-on';
import { Confirm } from '../shared/confirm';
import { describeError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
import { Notices } from '../shared/notices';
import { RelativeTime } from '../shared/relative-time';
import { injectRunLog } from './run-log';
import { RunPanel } from './run-panel';
import { describeSchedule } from './schedule';
import { WatchLog } from './watch-log';
import { WatchesApi } from './watches-api';

/** A watch: its settings and actions, its log of runs, and the run that's open beside the log. */
@Component({
  selector: 'app-watch-detail',
  imports: [
    RouterLink,
    RouterOutlet,
    WatchLog,
    RelativeTime,
    CdkMenu,
    CdkMenuItem,
    CdkMenuTrigger,
    LucideChevronLeft,
    LucideEllipsis,
    LucideEraser,
    LucidePencil,
    LucidePlay,
    LucideTrash2,
  ],
  templateUrl: './watch-detail.html',
  styleUrl: './watch-detail.scss',
})
export class WatchDetail {
  readonly id = input.required<string>();

  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);
  private readonly confirm = inject(Confirm);
  private readonly notices = inject(Notices);

  protected readonly watch = this.api.watchResource(this.id);
  /** The watch as last loaded, still shown if a refresh fails. */
  protected readonly shown = lastValue(this.watch);
  protected readonly log = injectRunLog(this.id);
  private readonly updates = inject(LiveUpdates);

  protected readonly missing = computed(() => {
    const error = this.watch.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  protected readonly schedule = computed(() => {
    const watch = this.shown();
    return watch ? describeSchedule(watch) : '';
  });

  /** The run panel, while a run is open. */
  private readonly panel = signal<RunPanel | null>(null);
  protected readonly runOpen = computed(() => this.panel() !== null);
  protected readonly selectedRunId = computed(() => this.panel()?.shownRunId() ?? null);

  protected readonly starting = signal(false);
  protected readonly describeError = describeError;

  /** The menu's button, which a dialog opened from the menu returns focus to. */
  private readonly moreActions = viewChild<ElementRef<HTMLElement>>('moreActions');
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  constructor() {
    const updates = this.updates;
    const isThisWatch = (watchId: number) => watchId === Number(this.id());
    // A new run moves the next scheduled run, so the header reloads too
    reloadOn(
      this.watch,
      updates.watchChanged.pipe(filter(isThisWatch)),
      updates.runUpdated.pipe(filter((update) => isThisWatch(update.watchId))),
      updates.reconnected,
    );

    // The page titles itself: the router would put its generic title back each time a run opens or closes
    const title = inject(Title);
    effect(() => {
      const watch = this.shown();
      title.setTitle(watch ? `${watch.name} – Lookout` : 'Watch – Lookout');
    });
  }

  protected panelOpened(component: unknown) {
    this.panel.set(component instanceof RunPanel ? component : null);
  }

  /** Gives focus back to the run's line in the log, or to the watch's heading if the run is gone, such as deleted. */
  protected panelClosed() {
    const closedRunId = this.selectedRunId();
    this.panel.set(null);
    afterNextRender(
      () => {
        const line = this.host.nativeElement.querySelector<HTMLElement>(`a.entry[href$="/runs/${closedRunId}"]`);
        (line ?? this.heading()?.nativeElement)?.focus();
      },
      { injector: this.injector },
    );
  }

  protected async runNow() {
    this.starting.set(true);
    try {
      const run = await this.api.run(this.id());
      this.updates.runCreatedHere(run);
      this.notices.say(`Started run #${run.id}.`);
    } catch (e) {
      this.notices.fail(`Couldn't start a run: ${describeError(e)}`);
    } finally {
      this.starting.set(false);
    }
  }

  protected async remove() {
    const name = this.shown()?.name ?? 'this watch';
    const sure = await this.confirm.ask({
      title: `Delete ${name}?`,
      body: 'Its script and every run are deleted for good.',
      confirmLabel: 'Delete watch',
      destructive: true,
      returnFocusTo: this.moreActions()?.nativeElement,
    });
    if (!sure) {
      return;
    }
    try {
      await this.api.delete(this.id());
      this.notices.say(`Deleted ${name}.`);
      await this.router.navigate(['/']);
    } catch (e) {
      this.notices.fail(`Couldn't delete ${name}: ${describeError(e)}`);
    }
  }

  protected async clearLog() {
    const sure = await this.confirm.ask({
      title: 'Delete every run?',
      body: 'The whole log is deleted for good. A run in progress is kept.',
      confirmLabel: 'Delete runs',
      destructive: true,
      returnFocusTo: this.moreActions()?.nativeElement,
    });
    if (!sure) {
      return;
    }
    try {
      const result = await this.api.deleteRuns(this.id());
      // Which runs went isn't said, so the log fetches its newest page again
      this.updates.runsDeletedHere(Number(this.id()), []);
      const kept = result.keptRunning > 0 ? ` Kept ${result.keptRunning} in progress.` : '';
      this.notices.say(`Deleted ${result.deleted} ${result.deleted === 1 ? 'run' : 'runs'}.${kept}`);
      if (this.runOpen()) {
        await this.router.navigate(['/watches', this.id()]);
      }
    } catch (e) {
      this.notices.fail(`Couldn't delete the runs: ${describeError(e)}`);
    }
  }
}
