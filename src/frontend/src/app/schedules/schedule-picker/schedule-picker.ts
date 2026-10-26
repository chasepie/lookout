import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { type FieldTree, FormField } from '@angular/forms/signals';
import { InstallTimeZone } from '../../settings/install-time-zone';
import type { ScheduleForm } from '../schedule';

/**
 * Chooses when a Watch runs: every N units, daily at a time, or a cron expression, plus optional jitter. Daily and cron
 * times are in the install's time zone, which it names.
 */
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
  private readonly timeZone = inject(InstallTimeZone).current;
  /** Where daily and cron times are, naming the time zone once it has loaded. */
  protected readonly zoneDescription = computed(() => {
    const timeZone = this.timeZone();
    return timeZone ? `Lookout's time zone, ${timeZone}` : 'Lookout\'s time zone';
  });
}
