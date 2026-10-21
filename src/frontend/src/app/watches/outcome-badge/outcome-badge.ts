import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { type RunOutcome, outcomeLabels } from '../watch.model';

/** A Run's outcome, as a badge coloured by whether it Succeeded. Its content prefixes the outcome. */
@Component({
  selector: 'app-outcome-badge',
  templateUrl: './outcome-badge.html',
  styleUrl: './outcome-badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.success]': 'outcome() === "succeeded"', '[class.failure]': 'outcome() !== "succeeded"' },
})
export class OutcomeBadge {
  readonly outcome = input.required<RunOutcome>();
  protected readonly outcomes = outcomeLabels;
}
