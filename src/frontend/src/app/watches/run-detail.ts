import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { filter, merge } from 'rxjs';
import { LiveUpdates } from './live-updates';
import { WatchRunStatus, WatchRunUpdate } from './watch.model';
import { describeError } from '../shared/describe-error';
import { WatchesApi } from './watches-api';

@Component({
  selector: 'app-run-detail',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a [routerLink]="['/watches', id()]">Back to watch</a></p>

    @if (run.isLoading() && !run.hasValue()) {
      <p>Loading…</p>
    } @else if (run.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (run.hasValue()) {
      <h2>Run #{{ run.value().id }}</h2>
      <p>
        <button type="button" (click)="run.reload()">Refresh</button>
        @if (run.value().status !== statuses.Running) {
          <button type="button" (click)="remove(run.value().id)">Delete run</button>
        }
      </p>
      @if (deleteError(); as deleteError) {
        <pre role="alert">{{ deleteError }}</pre>
      }
      <dl>
        <dt>Status</dt>
        <dd>{{ statuses[run.value().status] }}</dd>
        <dt>Created</dt>
        <dd>{{ run.value().createdTime | date: 'medium' }}</dd>
        <dt>Started</dt>
        <dd>{{ (run.value().startTime | date: 'medium') ?? '-' }}</dd>
        <dt>Ended</dt>
        <dd>{{ (run.value().endTime | date: 'medium') ?? '-' }}</dd>
      </dl>

      @if (run.value().error; as error) {
        <h3>Error</h3>
        <pre>{{ error }}</pre>
      }
      @if (run.value().content; as content) {
        <h3>Content</h3>
        <pre>{{ content }}</pre>
      }
      @if (screenshotUrl(); as src) {
        <h3>Screenshot</h3>
        <img [src]="src" alt="Screenshot captured during the run" />
      }
    }
  `,
  styles: `
    img {
      max-width: 100%;
    }
  `,
})
export class RunDetail {
  readonly id = input.required<string>();
  /** A run ID, or `"latest"`. */
  readonly runId = input.required<string>();

  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);

  protected readonly run = this.api.runResource(this.id, this.runId);
  protected readonly deleteError = signal<string | null>(null);
  protected readonly screenshotUrl = computed(() => {
    const screenshot = this.run.hasValue() ? this.run.value().screenshot : null;
    return screenshot ? `data:image/png;base64,${screenshot}` : null;
  });

  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;

  constructor() {
    // "latest" follows any run of this watch, since a new run becomes the latest one
    const updates = inject(LiveUpdates);
    const isThisRun = (update: WatchRunUpdate) =>
      update.watchId === Number(this.id()) &&
      (this.runId() === 'latest' || update.id === Number(this.runId()));

    const includesThisRun = (deleted: { watchId: number; runIds: number[] }) =>
      deleted.watchId === Number(this.id()) &&
      (this.runId() === 'latest' || deleted.runIds.includes(Number(this.runId())));

    merge(updates.runUpdated.pipe(filter(isThisRun)), updates.runsDeleted.pipe(filter(includesThisRun)), updates.reconnected)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.run.reload());
  }

  protected async remove(runId: number) {
    if (!confirm(`Delete run #${runId}?`)) {
      return;
    }
    try {
      this.deleteError.set(null);
      await this.api.deleteRun(this.id(), runId);
      await this.router.navigate(['/watches', this.id()]);
    } catch (e) {
      this.deleteError.set(describeError(e));
    }
  }
}
