import { Component, ElementRef, computed, inject, viewChild } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { LucideLogOut } from '@lucide/angular';
import { filter, map, pairwise } from 'rxjs';
import { Session } from './auth/session';
import { LiveIndicator } from './live/live-indicator';
import { describeError } from './shared/describe-error';
import { NoticeStrip } from './shared/notice-strip';
import { Notices } from './shared/notices';

/** The page a URL shows, ignoring an open run and the query, since opening a run doesn't change the page. */
function pageOf(url: string) {
  return url.split('?')[0].split('/runs/')[0];
}

@Component({
  imports: [RouterOutlet, RouterLink, LiveIndicator, NoticeStrip, LucideLogOut],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly session = inject(Session);
  private readonly notices = inject(Notices);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  private readonly url = toSignal(
    inject(Router).events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: '/' },
  );
  /** Which part of the app the page belongs to, for the navigation. */
  protected readonly section = computed(() => (this.url().startsWith('/vpn') ? 'vpn' : 'watches'));

  constructor() {
    // A new page moves focus to its content, as a full page load would, so screen readers start there
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        map((event) => pageOf(event.urlAfterRedirects)),
        pairwise(),
        filter(([previous, next]) => previous !== next),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.main().nativeElement.focus({ preventScroll: true }));
  }

  /**
   * Moves focus to the page's content. The link's own `#main` would load the home page instead, since links resolve
   * against `<base href="/">`.
   */
  protected skipToContent(event: Event) {
    event.preventDefault();
    this.main().nativeElement.focus();
  }

  protected signOut() {
    // On failure the session still works, so say so rather than appear signed out
    void this.session.signOut().catch((error: unknown) => {
      this.notices.fail(`Couldn't sign out, so you're still signed in: ${describeError(error)}`);
    });
  }
}
