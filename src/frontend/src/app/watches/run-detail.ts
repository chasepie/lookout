import { DatePipe } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WatchRunStatus } from './watch.model';
import { WatchesApi, describeError } from './watches-api';

@Component({
  selector: 'app-run-detail',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a [routerLink]="['/watches', id()]">Back to watch</a></p>

    @if (run.isLoading()) {
      <p>Loading…</p>
    } @else if (run.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (run.hasValue()) {
      <h2>Run #{{ run.value().id }}</h2>
      <p><button type="button" (click)="run.reload()">Refresh</button></p>
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

  protected readonly run = inject(WatchesApi).runResource(this.id, this.runId);
  protected readonly screenshotUrl = computed(() => {
    const screenshot = this.run.hasValue() ? this.run.value().screenshot : null;
    return screenshot ? `data:image/png;base64,${screenshot}` : null;
  });

  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;
}
