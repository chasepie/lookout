import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { LucideMaximize2, LucideTrash2, LucideX } from '@lucide/angular';
import { filter } from 'rxjs';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn } from '../live/reload-on';
import { Confirm } from '../shared/confirm';
import { describeError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
import { Notices } from '../shared/notices';
import { duration, fullTime } from '../shared/time';
import { RunOutcome } from './run-outcome';
import { outcomeOf, timingsOf } from './runs';
import { ScreenshotViewer, ScreenshotViewerData } from './screenshot-viewer';
import { WatchRunStatus, WatchRunUpdate } from './watch.model';
import { WatchesApi } from './watches-api';

/** Height over width beyond which the preview is cropped: a little taller than a 16:10 window. */
const tallestUncropped = 0.75;

/** One run in full: what it saw, how long it took, its screenshot and any error. Opens beside the watch's log. */
@Component({
  selector: 'app-run-panel',
  imports: [RouterLink, RunOutcome, LucideX, LucideTrash2, LucideMaximize2],
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
  private readonly updates = inject(LiveUpdates);
  private readonly dialog = inject(Dialog);

  protected readonly run = this.api.runResource(this.id, this.runId);
  /** The run as last loaded, still shown if a refresh fails. */
  protected readonly shown = lastValue(this.run);
  /** The ID of the run shown, which `"latest"` resolves to; the log highlights it. */
  readonly shownRunId = computed(() => this.shown()?.id ?? null);

  protected readonly missing = computed(() => {
    const error = this.run.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  protected readonly outcome = computed(() => {
    const run = this.shown();
    return run ? outcomeOf(run) : null;
  });
  protected readonly created = computed(() => {
    const run = this.shown();
    return run ? fullTime(Date.parse(run.createdTime)) : '';
  });
  protected readonly timings = computed(() => {
    const run = this.shown();
    if (!run) {
      return null;
    }
    const { waited, ran } = timingsOf(run);
    return { waited: waited === null ? null : duration(waited), ran: ran === null ? null : duration(ran) };
  });
  protected readonly screenshotUrl = computed(() => {
    const run = this.shown();
    return run?.screenshot ? `data:${run.screenshotMediaType ?? 'image/png'};base64,${run.screenshot}` : null;
  });
  /** The screenshot's own size, once it has loaded; a new screenshot starts unmeasured. */
  protected readonly screenshotSize = linkedSignal<string | null, { width: number; height: number } | null>({
    source: this.screenshotUrl,
    computation: () => null,
  });
  /**
   * Whether the preview shows only the top of the screenshot, as for a full page: it's much taller than the window
   * the page was in. The viewer shows the rest.
   */
  protected readonly cropped = computed(() => {
    const size = this.screenshotSize();
    return size !== null && size.width > 0 && size.height / size.width > tallestUncropped;
  });
  protected readonly deleting = signal(false);

  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;

  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  constructor() {
    // "latest" follows any run of this watch, since a new run becomes the latest one
    const updates = this.updates;
    const isThisRun = (update: WatchRunUpdate) =>
      update.watchId === Number(this.id()) && (this.runId() === 'latest' || update.id === Number(this.runId()));
    const includesThisRun = (deleted: { watchId: number; runIds: number[] }) =>
      deleted.watchId === Number(this.id()) &&
      (this.runId() === 'latest' || deleted.runIds.includes(Number(this.runId())));
    reloadOn(this.run, updates.runUpdated.pipe(filter(isThisRun)), updates.runsDeleted.pipe(filter(includesThisRun)), updates.reconnected);

    // Opening another run moves focus to it, so keyboard and screen reader users land on what they opened
    afterRenderEffect({
      write: () => {
        this.runId();
        this.heading()?.nativeElement.focus();
      },
    });
  }

  protected measureScreenshot(event: Event) {
    const image = event.target as HTMLImageElement;
    this.screenshotSize.set({ width: image.naturalWidth, height: image.naturalHeight });
  }

  protected viewScreenshot(src: string, runId: number, event: Event) {
    const size = this.screenshotSize();
    this.dialog.open<void, ScreenshotViewerData>(ScreenshotViewer, {
      data: {
        title: `Run #${runId} screenshot`,
        src,
        size: size ? `${size.width.toLocaleString()} × ${size.height.toLocaleString()} px` : null,
      },
      ariaLabelledBy: 'screenshot-viewer-title',
      panelClass: 'dialog-viewer',
      backdropClass: 'dialog-backdrop',
      // On the screenshot, so the arrow keys scroll it straight away
      autoFocus: '.scroll',
      // The preview, which Safari doesn't focus when it's clicked
      restoreFocus: event.currentTarget as HTMLElement,
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
      this.updates.runsDeletedHere(Number(this.id()), [runId]);
      this.notices.say(`Deleted run #${runId}.`);
      await this.router.navigate(['/watches', this.id()]);
    } catch (e) {
      this.notices.fail(`Couldn't delete run #${runId}: ${describeError(e)}`);
    } finally {
      this.deleting.set(false);
    }
  }
}
