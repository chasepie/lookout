import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, maxLength, minLength, pattern, required, submit, validate } from '@angular/forms/signals';
import { problemMessage } from './problem';
import { Session } from './session';

/** First-launch setup of Lookout's single account. The app shows it only while no account exists. */
@Component({
  selector: 'app-setup-form',
  imports: [FormField],
  templateUrl: './setup-form.html',
  styleUrl: './account-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SetupForm {
  private readonly session = inject(Session);
  protected readonly model = signal({ userName: '', password: '', confirmPassword: '' });
  protected readonly setupForm = form(this.model, (fields) => {
    required(fields.userName);
    pattern(fields.userName, /\S/, { message: 'Enter a user name.' });
    maxLength(fields.userName, 100);
    required(fields.password);
    minLength(fields.password, 8);
    maxLength(fields.password, 1024);
    validate(fields.confirmPassword, (field) =>
      field.value() === field.valueOf(fields.password) ? undefined : { kind: 'mismatch', message: 'Passwords do not match.' });
  });
  protected readonly error = signal('');

  protected async setUp() {
    await submit(this.setupForm, async () => {
      this.error.set('');
      try {
        const { userName, password } = this.model();
        await this.session.setUp(userName, password);
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not set up the account. Please try again.'));
      }
    });
  }
}
