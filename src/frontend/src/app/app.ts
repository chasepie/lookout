import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, apply, form, maxLength, pattern, required, submit } from '@angular/forms/signals';
import { LoginForm } from './login-form';
import { problemMessage } from './problem';
import { defaultSchedule, scheduleSchema } from './schedule';
import { ScheduleEditor } from './schedule-editor';
import { SchedulePicker } from './schedule-picker';
import { Session } from './session';
import { SetupForm } from './setup-form';
import type { RunOutcome, Watch } from './watch.model';
import { Watches } from './watches';

@Component({
  selector: 'app-root',
  imports: [DatePipe, DecimalPipe, FormField, LoginForm, ScheduleEditor, SchedulePicker, SetupForm],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly session = inject(Session);
  protected readonly watches = inject(Watches);
  protected readonly model = signal({ name: '', notificationUrl: '', schedule: defaultSchedule() });
  protected readonly watchForm = form(this.model, (fields) => {
    required(fields.name);
    pattern(fields.name, /\S/, { message: 'Enter a name.' });
    maxLength(fields.name, 200);
    required(fields.notificationUrl);
    pattern(fields.notificationUrl, /^https?:\/\/\S+$/i, { message: 'Enter an HTTP or HTTPS URL.' });
    maxLength(fields.notificationUrl, 2048);
    apply(fields.schedule, scheduleSchema);
  });
  protected readonly script = signal<File | undefined>(undefined);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly running = signal(new Set<string>());
  /** The Watch whose schedule is being edited, if any. */
  protected readonly editingSchedule = signal<string | undefined>(undefined);
  protected readonly outcomes: Record<RunOutcome, string> = {
    succeeded: 'Succeeded', knownFailure: 'Known failure', errored: 'Errored',
  };

  protected async logOut() {
    this.error.set('');
    this.notice.set('');
    try {
      await this.session.logOut();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not log out. Please try again.'));
    }
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
        this.watches.list.reload();
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not create the Watch. Check the fields and try again.'));
      }
    });
  }

  protected scheduleSaved({ name }: Watch) {
    this.editingSchedule.set(undefined);
    this.error.set('');
    this.notice.set(`Schedule of ${name} saved.`);
    this.watches.list.reload();
  }

  protected async runNow({ id, name }: Watch) {
    this.running.update((ids) => new Set(ids).add(id));
    this.error.set('');
    this.notice.set('');
    try {
      const run = await this.watches.runNow(id);
      // Announced through the status region; the reloaded list itself isn't live, so cards aren't re-read.
      this.notice.set(`Run of ${name} finished: ${this.outcomes[run.outcome]}.`);
      this.watches.list.reload();
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
