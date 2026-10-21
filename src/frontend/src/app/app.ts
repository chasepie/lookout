import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { LoginForm } from './session/login-form/login-form';
import { problemMessage } from './shared/problem';
import { Session } from './session/session';
import { SetupForm } from './session/setup-form/setup-form';

@Component({
  selector: 'app-root',
  imports: [LoginForm, RouterLink, RouterOutlet, SetupForm],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  protected readonly session = inject(Session);
  protected readonly error = signal('');

  protected async logOut() {
    this.error.set('');
    try {
      await this.session.logOut();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not log out. Please try again.'));
    }
  }
}
