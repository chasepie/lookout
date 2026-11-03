import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { merge } from 'rxjs';
import { LiveUpdates } from './live-updates';
import { WatchRunStatus, WatchScheduleKind } from './watch.model';
import { WatchesApi, describeError } from './watches-api';

@Component({
  selector: 'app-watch-list',
  imports: [RouterLink, DatePipe],
  template: `
    <h2>Watches</h2>
    <p><a routerLink="/watches/new">New watch</a></p>

    @if (watches.isLoading() && !watches.hasValue()) {
      <p>Loading…</p>
    } @else if (watches.error(); as error) {
      <pre role="alert">{{ describeError(error) }}</pre>
    } @else if (watches.hasValue()) {
      @if (watches.value().length === 0) {
        <p>No watches yet.</p>
      } @else {
        <table>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Schedule</th>
              <th scope="col">Latest run</th>
            </tr>
          </thead>
          <tbody>
            @for (watch of watches.value(); track watch.id) {
              <tr>
                <td><a [routerLink]="['/watches', watch.id]">{{ watch.name }}</a></td>
                <td>
                  @if (watch.scheduleEnabled && watch.scheduleKind !== null) {
                    {{ kinds[watch.scheduleKind] }} ({{ watch.scheduleInterval }})
                  } @else {
                    Off
                  }
                </td>
                <td>
                  @if (watch.watchRuns[0]; as run) {
                    {{ statuses[run.status] }}, {{ run.createdTime | date: 'medium' }}
                  } @else {
                    Never
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
})
export class WatchList {
  protected readonly watches = inject(WatchesApi).watchesResource();
  protected readonly kinds = WatchScheduleKind;
  protected readonly statuses = WatchRunStatus;
  protected readonly describeError = describeError;

  constructor() {
    // Any watch or run change can alter the list or its "Latest run" column
    const updates = inject(LiveUpdates);
    merge(updates.runUpdated, updates.runsDeleted, updates.watchChanged, updates.reconnected)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.watches.reload());
  }
}
