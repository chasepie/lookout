import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { Run } from '../watch.model';

/** When a Run finished and how long it took. */
@Component({
  selector: 'app-run-timing',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './run-timing.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunTiming {
  readonly run = input.required<Run>();
}
