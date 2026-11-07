import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, signal } from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { Router } from '@angular/router';
import { describeError } from '../shared/describe-error';
import { safeReturnUrl } from './auth-guards';
import { Session } from './session';

/** Signs the Owner in, then returns to the page they asked for. */
@Component({
  selector: 'app-login-page',
  imports: [FormField],
  template: `
    <section class="sheet" aria-labelledby="sign-in-title">
      <h1 id="sign-in-title">Sign in</h1>
      <p class="muted">Lookout has one Owner. Use the username and password it was started with.</p>
      <form class="form" novalidate (submit)="signIn($event)">
        <div class="field">
          <label class="field-label" for="username">Username</label>
          <input
            id="username"
            class="input"
            autocomplete="username"
            [formField]="loginForm.username"
            [attr.aria-invalid]="loginForm.username().touched() && loginForm.username().invalid()"
            aria-describedby="username-error"
          />
          <span id="username-error" class="field-error">
            @if (loginForm.username().touched() && loginForm.username().invalid()) {
              Enter your username.
            }
          </span>
        </div>
        <div class="field">
          <label class="field-label" for="password">Password</label>
          <input
            id="password"
            class="input"
            type="password"
            autocomplete="current-password"
            [formField]="loginForm.password"
            [attr.aria-invalid]="loginForm.password().touched() && loginForm.password().invalid()"
            aria-describedby="password-error"
          />
          <span id="password-error" class="field-error">
            @if (loginForm.password().touched() && loginForm.password().invalid()) {
              Enter your password.
            }
          </span>
        </div>
        @if (error(); as error) {
          <div class="callout callout-failure" role="alert"><p>{{ error }}</p></div>
        }
        <button type="submit" class="button button-primary" [disabled]="loginForm().submitting()">
          {{ loginForm().submitting() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </section>
  `,
  styles: `
    :host {
      display: grid;
      min-height: 60vh;
      place-items: center;
    }

    .sheet {
      display: grid;
      gap: var(--space-3);
      width: min(24rem, 100%);
    }

    .form {
      display: grid;
      gap: var(--space-4);
      margin-top: var(--space-4);
    }

    .button {
      justify-self: start;
    }
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
