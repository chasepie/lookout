import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

const baseUrl = '/api/auth';

/** The signed-in Owner, as the Backend's `/api/auth/me` returns it. */
interface OwnerInfo {
  username: string;
}

/**
 * Whether the Owner is signed in. The session itself is an HttpOnly cookie the browser sends with every request,
 * so this only tracks what the Backend last said about it.
 */
@Service()
export class Session {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  /** The Owner's username while signed in, `null` while signed out, and `undefined` until the Backend is asked. */
  private readonly owner = signal<string | null | undefined>(undefined);
  private pendingCheck: Promise<boolean> | null = null;

  readonly username = computed(() => this.owner() ?? null);
  readonly signedIn = computed(() => typeof this.owner() === 'string');

  /**
   * Whether there's a session, asking the Backend the first time only.
   * Rejects if the Backend can't be reached, leaving the answer unknown so the next call asks again.
   */
  check(): Promise<boolean> {
    if (this.owner() !== undefined) {
      return Promise.resolve(this.signedIn());
    }
    this.pendingCheck ??= this.fetchOwner().finally(() => (this.pendingCheck = null));
    return this.pendingCheck;
  }

  /**
   * Asks the Backend whether the session is still valid, such as after the live updates connection drops, and
   * handles it ending. Errors other than the session ending are ignored: the caller keeps retrying.
   */
  async recheck(): Promise<void> {
    try {
      if (!(await this.fetchOwner())) {
        this.expired();
      }
    } catch {
      // The Backend is unreachable, which says nothing about the session
    }
  }

  /** Rejects with the HTTP error when the credentials are wrong or attempts are rate-limited. */
  async signIn(username: string, password: string): Promise<void> {
    const owner = await firstValueFrom(this.http.post<OwnerInfo>(`${baseUrl}/login`, { username, password }));
    this.owner.set(owner.username);
  }

  async signOut(): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`${baseUrl}/logout`, null));
    } finally {
      // Signed out locally either way; an unreachable Backend can't use the cookie meanwhile anyway
      this.owner.set(null);
      await this.router.navigate(['/login']);
    }
  }

  /**
   * The Backend rejected the session: it expired, or the credentials changed. Sends the Owner to the login page,
   * back to the current page afterwards.
   */
  expired(): void {
    this.owner.set(null);
    const current = this.router.url;
    if (!current.startsWith('/login')) {
      void this.router.navigate(['/login'], { queryParams: { returnUrl: current } });
    }
  }

  /** Resolves to whether there's a session, and records the answer. */
  private async fetchOwner(): Promise<boolean> {
    try {
      const owner = await firstValueFrom(this.http.get<OwnerInfo>(`${baseUrl}/me`));
      this.owner.set(owner.username);
      return true;
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.owner.set(null);
        return false;
      }
      throw error;
    }
  }
}
