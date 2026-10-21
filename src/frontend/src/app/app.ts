import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationStart, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
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
  // About the last logout attempt, so it doesn't follow the user to other pages.
  protected readonly error = signal('');

  constructor() {
    inject(Router).events
      .pipe(filter((event) => event instanceof NavigationStart), takeUntilDestroyed())
      .subscribe(() => {
        this.error.set('');
      });
  }

  protected async logOut() {
    this.error.set('');
    try {
      await this.session.logOut();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not log out. Please try again.'));
    }
  }
}
