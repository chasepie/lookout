import { ChangeDetectionStrategy, Component, inject, input, linkedSignal, output, signal, untracked } from '@angular/core';
import { apply, form, submit } from '@angular/forms/signals';
import { problemMessage } from './problem';
import { type ScheduleForm, scheduleFormFor, scheduleSchema } from './schedule';
import { SchedulePicker } from './schedule-picker';
import type { Watch } from './watch.model';
import { Watches } from './watches';

/** Changes an existing Watch's schedule and jitter. */
@Component({
  selector: 'app-schedule-editor',
  imports: [SchedulePicker],
  templateUrl: './schedule-editor.html',
  styleUrl: './schedule-editor.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ScheduleEditor {
  private readonly watches = inject(Watches);
  readonly watch = input.required<Watch>();
  readonly saved = output<Watch>();
  readonly cancelled = output();
  protected readonly model = linkedSignal<string, { schedule: ScheduleForm }>({
    source: () => this.watch().id,
    // Reloading the Watches list passes in a fresh copy of the same Watch, which must keep the unsaved edits.
    computation: (id, previous) => previous?.source === id
      ? previous.value
      : untracked(() => ({ schedule: scheduleFormFor(this.watch().schedule, this.watch().jitterMinutes) })),
  });
  protected readonly editForm = form(this.model, (fields) => {
    apply(fields.schedule, scheduleSchema);
  });
  protected readonly error = signal('');

  protected async save() {
    await submit(this.editForm, async () => {
      this.error.set('');
      try {
        this.saved.emit(await this.watches.changeSchedule(this.watch().id, this.model().schedule));
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not save the schedule. Please try again.'));
      }
    });
  }
}
