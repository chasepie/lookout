import { DatePipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { WatchRunStatus, WatchScheduleKind } from './watch.model';
import { WatchesApi, describeError } from './watches-api';

@Component({
  selector: 'app-watch-detail',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a routerLink="/">All watches</a></p>

    @if (watch.isLoading()) {
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
    <p><a [routerLink]="['/watches', id(), 'runs', 'latest']">Latest run</a></p>
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
              <th scope="col">Created</th>
            </tr>
          </thead>
          <tbody>
            @for (run of runs.value(); track run.id) {
              <tr>
                <td><a [routerLink]="['/watches', id(), 'runs', run.id]">#{{ run.id }}</a></td>
                <td>{{ statuses[run.status] }}</td>
                <td>{{ run.createdTime | date: 'medium' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
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
