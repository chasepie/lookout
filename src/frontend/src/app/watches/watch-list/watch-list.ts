import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, apply, form, submit } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { OutcomeBadge } from '../outcome-badge/outcome-badge';
import { problemMessage } from '../../shared/problem';
import { RunTiming } from '../run-timing/run-timing';
import { defaultSchedule, scheduleSchema } from '../../schedules/schedule';
import { ScheduleEditor } from '../../schedules/schedule-editor/schedule-editor';
import { SchedulePicker } from '../../schedules/schedule-picker/schedule-picker';
import { StatusBadge } from '../status-badge/status-badge';
import { type Watch, outcomeLabels } from '../watch.model';
import { nameAndNotificationUrlRules } from '../watch-settings';
import { Watches } from '../watches';

@Component({
  selector: 'app-watch-list',
  imports: [DatePipe, FormField, OutcomeBadge, RouterLink, RunTiming, ScheduleEditor, SchedulePicker, StatusBadge],
  templateUrl: './watch-list.html',
  styleUrl: './watch-list.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchList {
  protected readonly watches = inject(Watches);
  protected readonly model = signal({ name: '', notificationUrl: '', schedule: defaultSchedule() });
  protected readonly watchForm = form(this.model, (fields) => {
    nameAndNotificationUrlRules(fields);
    apply(fields.schedule, scheduleSchema);
  });
  protected readonly script = signal<File | undefined>(undefined);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly running = signal(new Set<string>());
  /** The Watch whose schedule is being edited, if any. */
  protected readonly editingSchedule = signal<string | undefined>(undefined);
  protected readonly outcomes = outcomeLabels;

  constructor() {
    // Runs, including scheduled ones, and changes made on a Watch's page happen while the list isn't on screen.
    this.watches.refreshList();
  }

  protected selectScript(input: HTMLInputElement) {
    this.script.set(input.files?.[0]);
    this.error.set('');
  }

  protected async createWatch(input: HTMLInputElement) {
    await submit(this.watchForm, async () => {
      const script = this.script();
      if (!script || !/\.(ts|js)$/i.test(script.name) || script.size < 1 || script.size > 65536) {
        this.error.set('Choose a .ts or .js Script between 1 byte and 64 KiB.');
        return;
      }
      this.error.set('');
      this.notice.set('');
      try {
        const { name, notificationUrl, schedule } = this.model();
        await this.watches.create(name, notificationUrl, schedule, script);
        this.watchForm().reset({ name: '', notificationUrl: '', schedule: defaultSchedule() });
        this.script.set(undefined);
        input.value = '';
        this.notice.set('Watch created. It runs on its schedule, or you can run it now.');
        this.watches.refreshList();
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not create the Watch. Check the fields and try again.'));
      }
    });
  }

  protected scheduleSaved({ name }: Watch) {
    this.editingSchedule.set(undefined);
    this.error.set('');
    this.notice.set(`Schedule of ${name} saved.`);
    this.watches.refreshList();
  }

  protected async runNow({ id, name }: Watch) {
    this.running.update((ids) => new Set(ids).add(id));
    this.error.set('');
    this.notice.set('');
    try {
      const run = await this.watches.runNow(id);
      // Announced through the status region; the reloaded list itself isn't live, so cards aren't re-read.
      this.notice.set(`Run of ${name} finished: ${this.outcomes[run.outcome]}.`);
      this.watches.refreshList();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not run the Watch. Please try again.'));
    } finally {
      this.running.update((ids) => {
        const next = new Set(ids);
        next.delete(id);
        return next;
      });
    }
  }
}
