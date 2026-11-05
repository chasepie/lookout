import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { Router } from '@angular/router';
import { describeError } from '../watches/watches-api';
import { safeReturnUrl } from './auth-guards';
import { Session } from './session';

/** Signs the Owner in, then returns to the page they asked for. */
@Component({
  selector: 'app-login-page',
  imports: [FormField],
  template: `
    <h2>Sign in</h2>
    <form (submit)="signIn($event)">
      <p>
        <label for="username">Username</label><br />
        <input id="username" autocomplete="username" [formField]="loginForm.username" />
        @if (loginForm.username().touched() && loginForm.username().invalid()) {
          <br /><span role="alert">Username is required.</span>
        }
      </p>
      <p>
        <label for="password">Password</label><br />
        <input id="password" type="password" autocomplete="current-password" [formField]="loginForm.password" />
        @if (loginForm.password().touched() && loginForm.password().invalid()) {
          <br /><span role="alert">Password is required.</span>
        }
      </p>
      @if (error(); as error) {
        <p role="alert">{{ error }}</p>
      }
      <button type="submit" [disabled]="loginForm().submitting()">Sign in</button>
    </form>
  `,
})
export class LoginPage {
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  /** The page to return to, from the `returnUrl` query parameter. */
  readonly returnUrl = input<string>();

  protected readonly error = signal<string | null>(null);
  private readonly model = signal({ username: '', password: '' });
  protected readonly loginForm = form(this.model, (path) => {
    required(path.username);
    required(path.password);
  });

  protected signIn(event: Event) {
    event.preventDefault();
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.loginForm, async () => {
      this.error.set(null);
      try {
        const { username, password } = this.model();
        await this.session.signIn(username, password);
        await this.router.navigateByUrl(safeReturnUrl(this.returnUrl() ?? null));
      } catch (e) {
        this.error.set(describeSignInError(e));
      }
      return undefined;
    });
  }
}

function describeSignInError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 429) {
      return 'Too many sign-in attempts. Wait a minute, then try again.';
    }
    if (error.status === 0) {
      return "Lookout can't be reached. Check that it's running, then try again.";
    }
  }
  // The Backend's own message, such as "Username or password is incorrect."
  return describeError(error);
}
