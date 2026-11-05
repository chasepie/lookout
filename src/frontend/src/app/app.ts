import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Session } from './auth/session';
import { describeError } from './shared/describe-error';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly session = inject(Session);
  protected readonly signOutError = signal<string | null>(null);

  protected signOut() {
    this.signOutError.set(null);
    // On failure the session still works, so say so rather than appear signed out
    void this.session.signOut().catch((error: unknown) => {
      this.signOutError.set(`Couldn't sign out, so you're still signed in: ${describeError(error)}`);
    });
  }
}
