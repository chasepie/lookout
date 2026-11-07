import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { LucidePlus } from '@lucide/angular';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn, settle } from '../live/reload-on';
import { buffer, map, merge } from 'rxjs';
import { describeError } from '../shared/describe-error';
import { lastValue } from '../shared/last-value';
import { WatchSummary } from './watch.model';
import { WatchRow } from './watch-row';
import { WatchesApi } from './watches-api';

/** Every watch, each with what it last saw. */
@Component({
  selector: 'app-watch-list',
  imports: [RouterLink, WatchRow, LucidePlus],
  template: `
    <header class="page-head">
      <h1>Watches</h1>
      @if (shown()?.length) {
        <a class="button button-primary" routerLink="/watches/new">
          <svg lucidePlus aria-hidden="true" />
          New watch
        </a>
      }
    </header>

    @if (shown(); as list) {
      @if (watches.error(); as error) {
        <div class="callout callout-failure callout-retry" role="alert">
          <p>Couldn't refresh the watches: {{ describeError(error) }}</p>
          <button type="button" class="button" (click)="watches.reload()">Try again</button>
        </div>
      }
      @if (list.length === 0) {
        <section class="empty" aria-labelledby="empty-title">
          <h2 id="empty-title">Nothing to watch yet</h2>
          <p>
            A watch opens a page with a script you write, on a schedule or when you ask, and keeps what the script
            reports each time.
          </p>
          <a class="button button-primary" routerLink="/watches/new">
            <svg lucidePlus aria-hidden="true" />
            New watch
          </a>
        </section>
      } @else {
        <ol class="watches">
          @for (watch of list; track watch.id) {
            <li><app-watch-row [watch]="watch" /></li>
          }
        </ol>
      }
    } @else if (watches.error(); as error) {
      <div class="callout callout-failure callout-retry" role="alert">
        <p>Couldn't load the watches: {{ describeError(error) }}</p>
        <button type="button" class="button" (click)="watches.reload()">Try again</button>
      </div>
    } @else {
      <p class="muted" role="status">Loading watches…</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-5);
    }

    .page-head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-4);
    }

    .watches {
      list-style: none;
      border-top: 1px solid var(--rule);
    }

    .watches > li {
      border-bottom: 1px solid var(--rule);
    }

    .empty {
      display: grid;
      justify-items: start;
      gap: var(--space-4);
      padding: var(--space-7) 0;
      border-top: 1px solid var(--rule);
    }
  `,
})
export class WatchList {
  private readonly api = inject(WatchesApi);
  protected readonly watches = this.api.watchesResource();
  /** The list as last loaded, still shown if a refresh fails. */
  protected readonly shown = lastValue(this.watches);
  protected readonly describeError = describeError;

  constructor() {
    const updates = inject(LiveUpdates);
    // A watch added, changed or removed, or messages missed while disconnected: load the whole list again
    reloadOn(this.watches, updates.watchChanged, updates.reconnected);

    // A run only changes its own watch's row, so fetch just those rows, once a burst of runs settles
    const runWatchIds = merge(updates.runUpdated, updates.runsDeleted).pipe(map((update) => update.watchId));
    runWatchIds
      .pipe(buffer(runWatchIds.pipe(settle())), takeUntilDestroyed())
      .subscribe((watchIds) => void this.refreshRows(new Set(watchIds)));
  }

  private async refreshRows(watchIds: Set<number>) {
    if (!this.watches.hasValue()) {
      this.watches.reload();
      return;
    }
    for (const id of watchIds) {
      try {
        const summary = await this.api.watchSummary(id);
        this.replaceRow(id, summary);
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.status === 404) {
          this.replaceRow(id, null);
        }
        // Anything else leaves the row as it was, for the next change to bring up to date
      }
    }
  }

  /** Puts a watch's row in place, or takes it out when `summary` is null. */
  private replaceRow(id: number, summary: WatchSummary | null) {
    if (!this.watches.hasValue()) {
      return;
    }
    this.watches.value.update((list) =>
      summary ? list.map((watch) => (watch.id === id ? summary : watch)) : list.filter((watch) => watch.id !== id),
    );
  }
}
