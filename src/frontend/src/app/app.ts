import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, maxLength, pattern, required, submit } from '@angular/forms/signals';
import { LoginForm } from './login-form';
import { problemMessage } from './problem';
import { Session } from './session';
import { SetupForm } from './setup-form';
import type { RunOutcome, Watch } from './watch.model';
import { Watches } from './watches';

@Component({
  selector: 'app-root',
  imports: [DatePipe, DecimalPipe, FormField, LoginForm, SetupForm],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly session = inject(Session);
  protected readonly watches = inject(Watches);
  protected readonly model = signal({ name: '', notificationUrl: '' });
  protected readonly watchForm = form(this.model, (fields) => {
    required(fields.name);
    pattern(fields.name, /\S/, { message: 'Enter a name.' });
    maxLength(fields.name, 200);
    required(fields.notificationUrl);
    pattern(fields.notificationUrl, /^https?:\/\/\S+$/i, { message: 'Enter an HTTP or HTTPS URL.' });
    maxLength(fields.notificationUrl, 2048);
  });
  protected readonly script = signal<File | undefined>(undefined);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly running = signal(new Set<string>());
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
        const { name, notificationUrl } = this.model();
        await this.watches.create(name, notificationUrl, script);
        this.watchForm().reset({ name: '', notificationUrl: '' });
        this.script.set(undefined);
        input.value = '';
        this.notice.set('Watch created. Run it now to read its first content.');
        this.watches.list.reload();
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not create the Watch. Check the fields and try again.'));
      }
    });
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
