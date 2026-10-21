import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { type FieldTree, FormField } from '@angular/forms/signals';
import type { ScheduleForm } from '../schedule';

/** Chooses when a Watch runs: every N units, daily at a time, or a cron expression, plus optional jitter. */
@Component({
  selector: 'app-schedule-picker',
  imports: [FormField],
  templateUrl: './schedule-picker.html',
  styleUrl: './schedule-picker.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SchedulePicker {
  private static count = 0;
  readonly schedule = input.required<FieldTree<ScheduleForm>>();
  /** Keeps labels pointing at their own inputs when a page shows more than one picker. */
  protected readonly id = `schedule-${++SchedulePicker.count}`;
}
