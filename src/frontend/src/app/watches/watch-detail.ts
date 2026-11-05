import { DatePipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { filter, merge } from 'rxjs';
import { LiveUpdates } from './live-updates';
import { WatchRunStatus, WatchScheduleKind } from './watch.model';
import { describeError } from '../shared/describe-error';
import { WatchesApi } from './watches-api';

@Component({
  selector: 'app-watch-detail',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a routerLink="/">All watches</a></p>

    @if (watch.isLoading() && !watch.hasValue()) {
      <p>Loading…</p>
    } @else if (watch.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (watch.hasValue()) {
      <h2>{{ watch.value().name }}</h2>
      <dl>
        <dt>ID</dt>
        <dd>{{ watch.value().id }}</dd>
        <dt>Schedule</dt>
        <dd>
          @if (watch.value().scheduleEnabled && watch.value().scheduleKind !== null) {
            {{ kinds[watch.value().scheduleKind!] }} ({{ watch.value().scheduleInterval }})
          } @else {
            Off
          }
        </dd>
        <dt>VPN</dt>
        <dd>{{ watch.value().useVpn ? 'On' : 'Off' }}</dd>
      </dl>

      <p>
        <a [routerLink]="['/watches', id(), 'edit']">Edit</a>
        <button type="button" (click)="run()">Run now</button>
        <button type="button" (click)="remove()">Delete</button>
      </p>

      <h3>Script</h3>
      <form (submit)="upload($event)">
        <label for="script">Script file</label>
        <input id="script" type="file" (change)="pickFile($event)" />
        <button type="submit" [disabled]="!file()">Upload</button>
      </form>
    }

    @if (message(); as message) {
      <pre role="status">{{ message }}</pre>
    }

    <h3>Runs</h3>
    <p>
      <a [routerLink]="['/watches', id(), 'runs', 'latest']">Latest run</a>
      @if (runs.hasValue() && runs.value().length > 0) {
        <button type="button" (click)="deleteAllRuns()">Delete all runs</button>
      }
    </p>
    @if (runs.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (runs.hasValue()) {
      @if (runs.value().length === 0) {
        <p>No runs yet.</p>
      } @else {
        <table>
          <thead>
            <tr>
              <th scope="col">Run</th>
              <th scope="col">Status</th>
              <th scope="col">Content</th>
              <th scope="col">Created</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            @for (run of runs.value(); track run.id) {
              <tr>
                <td><a [routerLink]="['/watches', id(), 'runs', run.id]">#{{ run.id }}</a></td>
                <td>{{ statuses[run.status] }}</td>
                @if (run.content; as content) {
                  <td class="content" [title]="content">{{ content }}</td>
                } @else if (run.error; as error) {
                  <td class="content error" [title]="error">Error: {{ error }}</td>
                } @else {
                  <td>-</td>
                }
                <td>{{ run.createdTime | date: 'medium' }}</td>
                <td>
                  @if (run.status !== statuses.Running) {
                    <button type="button" [attr.aria-label]="'Delete run #' + run.id" (click)="deleteRun(run.id)">
                      Delete
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
  styles: `
    /* Long output is cut to one line; the run's page and the tooltip show all of it */
    .content {
      max-width: 40ch;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .error {
      color: #b00020;
    }
  `,
})
export class WatchDetail {
  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);

  readonly id = input.required<string>();

  protected readonly watch = this.api.watchResource(this.id);
  protected readonly runs = this.api.runsResource(this.id);
  protected readonly file = signal<File | null>(null);
  protected readonly message = signal<string | null>(null);

  protected readonly kinds = WatchScheduleKind;
  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;

  constructor() {
    const updates = inject(LiveUpdates);
    const isThisWatch = (watchId: number) => watchId === Number(this.id());

    updates.watchChanged
      .pipe(filter(isThisWatch), takeUntilDestroyed())
      .subscribe(() => this.watch.reload());
    merge(updates.runUpdated, updates.runsDeleted)
      .pipe(filter((update) => isThisWatch(update.watchId)), takeUntilDestroyed())
      .subscribe(() => this.runs.reload());
    updates.reconnected.pipe(takeUntilDestroyed()).subscribe(() => {
      this.watch.reload();
      this.runs.reload();
    });
  }

  protected async run() {
    try {
      const run = await this.api.run(this.id());
      this.message.set(`Created run #${run.id}.`);
      this.runs.reload();
    } catch (e) {
      this.message.set(describeError(e));
    }
  }

  protected async remove() {
    if (!confirm('Delete this watch?')) {
      return;
    }
    try {
      await this.api.delete(this.id());
      await this.router.navigate(['/']);
    } catch (e) {
      this.message.set(describeError(e));
    }
  }

  protected async deleteRun(runId: number) {
    if (!confirm(`Delete run #${runId}?`)) {
      return;
    }
    try {
      await this.api.deleteRun(this.id(), runId);
      this.message.set(`Deleted run #${runId}.`);
      this.runs.reload();
    } catch (e) {
      this.message.set(describeError(e));
    }
  }

  protected async deleteAllRuns() {
    if (!confirm('Delete all runs of this watch? Runs in progress are kept.')) {
      return;
    }
    try {
      const result = await this.api.deleteRuns(this.id());
      const kept = result.keptRunning > 0 ? ` Kept ${result.keptRunning} in progress.` : '';
      this.message.set(`Deleted ${result.deleted} runs.${kept}`);
      this.runs.reload();
    } catch (e) {
      this.message.set(describeError(e));
    }
  }

  protected pickFile(event: Event) {
    const input = event.target as HTMLInputElement;
    this.file.set(input.files?.[0] ?? null);
  }

  protected async upload(event: Event) {
    event.preventDefault();
    const file = this.file();
    if (!file) {
      return;
    }
    try {
      await this.api.uploadScript(this.id(), file);
      this.message.set(`Uploaded ${file.name}.`);
      this.watch.reload();
    } catch (e) {
      this.message.set(describeError(e));
    }
  }
}
