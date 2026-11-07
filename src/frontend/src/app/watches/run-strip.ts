import { Component, computed, input } from '@angular/core';
import { clockTime } from '../shared/time';
import { outcomeOf, repeats } from './runs';
import { WatchRunSummary } from './watch.model';

const tickWidth = 4;
const gap = 2;
const baseline = 16;
const height = 24;

interface Tick {
  id: number;
  x: number;
  y: number;
  height: number;
  kind: 'changed' | 'same' | 'failed' | 'running' | 'queued';
  title: string;
}

/**
 * A watch's recent runs as a row of soundings, oldest on the left: a tall mark where the result changed, a short one
 * where it repeated, and a mark hanging below the line where a run failed. Position and length carry the meaning,
 * so it reads without colour; the summary says the same in words.
 */
@Component({
  selector: 'app-run-strip',
  template: `
    <svg
      role="img"
      [attr.aria-label]="summary()"
      [attr.width]="width()"
      [attr.height]="height"
      [attr.viewBox]="'0 0 ' + width() + ' ' + height"
    >
      <line class="baseline" x1="0" [attr.x2]="width()" [attr.y1]="baseline + 0.5" [attr.y2]="baseline + 0.5" />
      @for (tick of ticks(); track tick.id) {
        <rect
          [attr.class]="tick.kind"
          [attr.x]="tick.x"
          [attr.y]="tick.y"
          [attr.width]="tickWidth"
          [attr.height]="tick.height"
          rx="1"
        >
          <title>{{ tick.title }}</title>
        </rect>
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
      line-height: 0;
    }

    .baseline {
      stroke: var(--rule);
      stroke-width: 1;
    }

    .changed {
      fill: var(--ink);
    }

    .same {
      fill: var(--ink-muted);
    }

    .failed {
      fill: var(--magenta);
    }

    .running {
      fill: var(--deep);
      animation: sweep 1.6s ease-in-out infinite;
    }

    .queued {
      fill: none;
      stroke: var(--deep);
      stroke-width: 1;
    }

    @keyframes sweep {
      50% {
        opacity: 0.3;
      }
    }
  `,
})
export class RunStrip {
  /** Newest first, as the API sends them. */
  readonly runs = input.required<WatchRunSummary[]>();
  /** How many places the strip has, so strips line up whatever each watch's history. */
  readonly slots = input(24);

  protected readonly tickWidth = tickWidth;
  protected readonly height = height;
  protected readonly baseline = baseline;

  protected readonly width = computed(() => this.slots() * (tickWidth + gap) - gap);

  protected readonly ticks = computed<Tick[]>(() => {
    const runs = this.runs().slice(0, this.slots());
    const same = repeats(runs);
    // Right-aligned, so the newest run is always at the same place
    const offset = this.slots() - runs.length;
    return runs
      .map((run, i) => {
        const outcome = outcomeOf(run);
        const kind: Tick['kind'] =
          outcome === 'failed'
            ? 'failed'
            : outcome === 'running'
              ? 'running'
              : outcome === 'queued'
                ? 'queued'
                : same[i]
                  ? 'same'
                  : 'changed';
        const slot = offset + (runs.length - 1 - i);
        const time = clockTime(Date.parse(run.createdTime));
        const shape = {
          changed: { y: 2, height: baseline - 2 },
          same: { y: baseline - 6, height: 6 },
          failed: { y: baseline + 2, height: height - baseline - 2 },
          running: { y: 2, height: baseline - 2 },
          queued: { y: baseline - 6.5, height: 6 },
        }[kind];
        const what = {
          changed: run.content ?? 'Finished',
          same: `Same as before: ${run.content ?? ''}`,
          failed: `Failed: ${(run.error ?? '').split('\n')[0]}`,
          running: 'Running',
          queued: 'Waiting to start',
        }[kind];
        return { id: run.id, x: slot * (tickWidth + gap), ...shape, kind, title: `Run #${run.id} at ${time}. ${what}` };
      })
      .reverse();
  });

  protected readonly summary = computed(() => {
    const ticks = this.ticks();
    if (ticks.length === 0) {
      return 'No runs yet';
    }
    const failed = ticks.filter((t) => t.kind === 'failed').length;
    const changed = ticks.filter((t) => t.kind === 'changed').length;
    const parts = [`Last ${ticks.length} ${ticks.length === 1 ? 'run' : 'runs'}`];
    parts.push(failed === 0 ? 'none failed' : `${failed} failed`);
    parts.push(changed === 1 ? '1 new result' : `${changed} new results`);
    return parts.join(', ');
  });
}
