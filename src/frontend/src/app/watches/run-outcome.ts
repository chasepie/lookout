import { Component, computed, input } from '@angular/core';
import { RunOutcome as Outcome, Run, outcomeLabels, outcomeOf } from './runs';

/**
 * What a run saw, in the serif reserved for what watched pages said, or why it has nothing to show.
 * A repeat of the run before shows a ditto mark, as a logbook would, so changes stand out.
 */
@Component({
  selector: 'app-run-outcome',
  template: `
    @switch (outcome()) {
      @case ('observed') {
        @if (repeat()) {
          <span class="ditto" aria-hidden="true" [title]="run().content">″</span>
          <span class="visually-hidden">Same as above: {{ run().content }}</span>
        } @else {
          <span class="observation" [class.clamped]="clamp()">{{ run().content }}</span>
        }
      }
      @case ('crashed') {
        <span class="failure" [class.clamped]="clamp()">
          <span class="failure-label">{{ labels.crashed }}</span> {{ firstLine() }}
        </span>
      }
      @case ('knownFailure') {
        <span class="known-failure" [class.clamped]="clamp()">
          <span class="failure-label">{{ labels.knownFailure }}</span> {{ run().failureReason }}
        </span>
      }
      @case ('running') {
        <span class="running">{{ labels.running }}</span>
      }
      @default {
        <span class="muted">{{ label() }}</span>
      }
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }

    .clamped {
      display: -webkit-box;
      -webkit-box-orient: vertical;
      -webkit-line-clamp: var(--clamp-lines, 2);
      line-clamp: var(--clamp-lines, 2);
      overflow: hidden;
    }

    // Set upright, as a ditto mark is written, a little in from where the text above starts
    .ditto {
      padding-left: 1em;
      color: var(--ink-muted);
      font-family: var(--font-observation);
      font-style: normal;
      line-height: 1;
    }

    .failure-label {
      font-weight: 600;
    }

    // Quieter than a crash: the script did its job, and said why there's nothing to show
    .known-failure {
      color: var(--ink-muted);
      white-space: pre-line;

      .failure-label {
        color: var(--ink);
      }
    }

    .running {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      color: var(--deep);
      font-weight: 500;

      &::before {
        content: '';
        width: 0.5rem;
        height: 0.5rem;
        border-radius: 50%;
        background: currentColor;
        animation: pulse 1.6s ease-in-out infinite;
      }
    }

  `,
})
export class RunOutcome {
  readonly run = input.required<Run>();
  /** Whether the run saw the same as the run listed above it, which shows a ditto mark. */
  readonly repeat = input(false);
  /** Whether to cut long content or a failure to a few lines; `--clamp-lines` sets how many. */
  readonly clamp = input(true);

  protected readonly outcome = computed<Outcome>(() => outcomeOf(this.run()));
  protected readonly labels = outcomeLabels;
  protected readonly label = computed(() => {
    const outcome = this.outcome();
    return outcome === 'observed' ? '' : outcomeLabels[outcome];
  });
  /** A crash's first line says what went wrong, as a stack trace's does; the run's panel shows the rest. */
  protected readonly firstLine = computed(() => (this.run().error ?? '').split('\n')[0]);
}
