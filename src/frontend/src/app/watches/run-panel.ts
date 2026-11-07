import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, afterRenderEffect, computed, inject, input, signal, viewChild } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { LucideTrash2, LucideX } from '@lucide/angular';
import { filter } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn } from '../live/reload-on';
import { Confirm } from '../shared/confirm';
import { describeError } from '../shared/describe-error';
import { Notices } from '../shared/notices';
import { duration, fullTime } from '../shared/time';
import { RunOutcome } from './run-outcome';
import { outcomeOf, timingsOf } from './runs';
import { WatchRunStatus, WatchRunUpdate } from './watch.model';
import { WatchesApi } from './watches-api';

/** One run in full: what it saw, how long it took, its screenshot and any error. Opens beside the watch's log. */
@Component({
  selector: 'app-run-panel',
  imports: [RouterLink, RunOutcome, LucideX, LucideTrash2],
  templateUrl: './run-panel.html',
  styleUrl: './run-panel.scss',
})
export class RunPanel {
  readonly id = input.required<string>();
  /** A run ID, or `"latest"`. */
  readonly runId = input.required<string>();

  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);
  private readonly confirm = inject(Confirm);
  private readonly notices = inject(Notices);

  protected readonly run = this.api.runResource(this.id, this.runId);
  /** The ID of the run shown, which `"latest"` resolves to; the log highlights it. */
  readonly shownRunId = computed(() => (this.run.hasValue() ? this.run.value().id : null));

  protected readonly missing = computed(() => {
    const error = this.run.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  protected readonly outcome = computed(() => (this.run.hasValue() ? outcomeOf(this.run.value()) : null));
  protected readonly created = computed(() => (this.run.hasValue() ? fullTime(Date.parse(this.run.value().createdTime)) : ''));
  protected readonly timings = computed(() => {
    if (!this.run.hasValue()) {
      return null;
    }
    const { waited, ran } = timingsOf(this.run.value());
    return { waited: waited === null ? null : duration(waited), ran: ran === null ? null : duration(ran) };
  });
  protected readonly screenshotUrl = computed(() => {
    const screenshot = this.run.hasValue() ? this.run.value().screenshot : null;
    return screenshot ? `data:image/png;base64,${screenshot}` : null;
  });
  /** Whether the screenshot is shown at its own size, scrolling, rather than fitted to the panel. */
  protected readonly actualSize = signal(false);
  protected readonly deleting = signal(false);

  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;

  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  constructor() {
    // "latest" follows any run of this watch, since a new run becomes the latest one
    const updates = inject(LiveUpdates);
    const isThisRun = (update: WatchRunUpdate) =>
      update.watchId === Number(this.id()) && (this.runId() === 'latest' || update.id === Number(this.runId()));
    const includesThisRun = (deleted: { watchId: number; runIds: number[] }) =>
      deleted.watchId === Number(this.id()) &&
      (this.runId() === 'latest' || deleted.runIds.includes(Number(this.runId())));
    reloadOn(this.run, updates.runUpdated.pipe(filter(isThisRun)), updates.runsDeleted.pipe(filter(includesThisRun)), updates.reconnected);

    // Opening another run moves focus to it, so keyboard and screen reader users land on what they opened
    afterRenderEffect(() => {
      this.runId();
      this.heading()?.nativeElement.focus({ preventScroll: false });
    });
  }

  protected async remove(runId: number) {
    const sure = await this.confirm.ask({
      title: `Delete run #${runId}?`,
      body: 'Its result, error and screenshot are deleted for good.',
      confirmLabel: 'Delete run',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    this.deleting.set(true);
    try {
      await this.api.deleteRun(this.id(), runId);
      this.notices.say(`Deleted run #${runId}.`);
      await this.router.navigate(['/watches', this.id()]);
    } catch (e) {
      this.notices.fail(`Couldn't delete run #${runId}: ${describeError(e)}`);
    } finally {
      this.deleting.set(false);
    }
  }
}
