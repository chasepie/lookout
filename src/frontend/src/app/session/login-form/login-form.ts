import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { problemMessage } from '../../shared/problem';
import { Session } from '../session';

@Component({
  selector: 'app-login-form',
  imports: [FormField],
  templateUrl: './login-form.html',
  styleUrl: '../account-form.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginForm {
  private readonly session = inject(Session);
  protected readonly model = signal({ userName: '', password: '' });
  protected readonly loginForm = form(this.model, (fields) => {
    required(fields.userName);
    required(fields.password);
  });
  protected readonly error = signal('');

  protected async logIn() {
    await submit(this.loginForm, async () => {
      this.error.set('');
      try {
        const { userName, password } = this.model();
        await this.session.logIn(userName, password);
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not log in. Please try again.'));
      }
    });
  }
}
