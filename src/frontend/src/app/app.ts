import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, maxLength, pattern, required, submit } from '@angular/forms/signals';
import { Watches } from './watches';
import type { RunOutcome } from './watch.model';

@Component({
  selector: 'app-root',
  imports: [DatePipe, DecimalPipe, FormField],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
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
      } catch {
        this.error.set('Could not create the Watch. Check the fields and try again.');
      }
    });
  }

  protected async runNow(id: string) {
    this.running.update((ids) => new Set(ids).add(id));
    this.error.set('');
    this.notice.set('');
    try {
      await this.watches.runNow(id);
      this.watches.list.reload();
    } catch {
      this.error.set('Could not run the Watch. Please try again.');
    } finally {
      this.running.update((ids) => {
        const next = new Set(ids);
        next.delete(id);
        return next;
      });
    }
  }
}
