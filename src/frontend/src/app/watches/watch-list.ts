import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucidePlus } from '@lucide/angular';
import { LiveUpdates } from '../live/live-updates';
import { reloadOn } from '../live/reload-on';
import { describeError } from '../shared/describe-error';
import { WatchRow } from './watch-row';
import { WatchesApi } from './watches-api';

/** Every watch, each with what it last saw. */
@Component({
  selector: 'app-watch-list',
  imports: [RouterLink, WatchRow, LucidePlus],
  template: `
    <header class="page-head">
      <h1>Watches</h1>
      @if (watches.hasValue() && watches.value().length > 0) {
        <a class="button button-primary" routerLink="/watches/new">
          <svg lucidePlus aria-hidden="true"></svg>
          New watch
        </a>
      }
    </header>

    @if (watches.isLoading() && !watches.hasValue()) {
      <p class="muted" role="status">Loading watches…</p>
    } @else if (watches.error(); as error) {
      <div class="callout callout-failure" role="alert">
        <p>Couldn't load the watches: {{ describeError(error) }}</p>
      </div>
    } @else if (watches.hasValue()) {
      @if (watches.value().length === 0) {
        <section class="empty" aria-labelledby="empty-title">
          <h2 id="empty-title">Nothing to watch yet</h2>
          <p>
            A watch opens a page with a script you write, on a schedule or when you ask, and keeps what the script
            reports each time.
          </p>
          <a class="button button-primary" routerLink="/watches/new">
            <svg lucidePlus aria-hidden="true"></svg>
            New watch
          </a>
        </section>
      } @else {
        <ol class="watches">
          @for (watch of watches.value(); track watch.id) {
            <li><app-watch-row [watch]="watch" /></li>
          }
        </ol>
      }
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
  protected readonly watches = inject(WatchesApi).watchesResource();
  protected readonly describeError = describeError;

  constructor() {
    // Any watch or run change can alter the list or what a row last saw
    const updates = inject(LiveUpdates);
    reloadOn(this.watches, updates.runUpdated, updates.runsDeleted, updates.watchChanged, updates.reconnected);
  }
}
