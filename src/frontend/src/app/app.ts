import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Session } from './auth/session';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly session = inject(Session);

  protected signOut() {
    // Signing out always ends locally and goes to the login page, even if the Backend can't be reached
    void this.session.signOut().catch(() => undefined);
  }
}
