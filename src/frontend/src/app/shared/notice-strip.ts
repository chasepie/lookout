import { Component, computed, inject } from '@angular/core';
import { LucideX } from '@lucide/angular';
import { Notices } from './notices';

/**
 * Where notices appear, at the bottom of the window. Both regions are always in the page, empty or not, so screen
 * readers announce what's added to them.
 */
@Component({
  selector: 'app-notice-strip',
  imports: [LucideX],
  template: `
    <div role="status" class="region">
      @for (notice of info(); track notice.id) {
        <p class="notice" animate.enter="notice-enter" animate.leave="notice-leave">{{ notice.text }}</p>
      }
    </div>
    <div role="alert" class="region">
      @for (notice of failures(); track notice.id) {
        <div class="notice notice-failure" animate.enter="notice-enter" animate.leave="notice-leave">
          <p>{{ notice.text }}</p>
          <button type="button" class="button button-quiet button-icon" (click)="notices.dismiss(notice.id)">
            <svg lucideX aria-hidden="true" />
            <span class="visually-hidden">Dismiss</span>
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      position: fixed;
      inset: auto var(--gutter) var(--space-4) auto;
      z-index: 900;
      display: grid;
      gap: var(--space-2);
      width: min(26rem, calc(100vw - 2 * var(--gutter)));
      pointer-events: none;
    }

    .region {
      display: grid;
      gap: var(--space-2);
    }

    .notice {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--space-3);
      margin: 0;
      padding: var(--space-3) var(--space-4);
      border-left: 3px solid var(--deep);
      border-radius: var(--radius-control);
      background: var(--paper-raised);
      box-shadow: var(--shadow);
      pointer-events: auto;
      white-space: pre-line;
    }

    // Answers the action that raised it: slides in, and fades as it goes
    .notice-enter {
      animation: arrive 200ms var(--ease-out);
    }

    .notice-leave {
      animation: depart 150ms ease-in forwards;
    }

    .notice-failure {
      border-left-color: var(--magenta);
      padding-right: var(--space-1);

      p {
        padding-top: var(--space-2);
      }
    }

    @keyframes arrive {
      from {
        opacity: 0;
        transform: translateY(0.5rem);
      }
    }

    @keyframes depart {
      to {
        opacity: 0;
      }
    }
  `,
})
export class NoticeStrip {
  protected readonly notices = inject(Notices);
  protected readonly info = computed(() => this.notices.all().filter((n) => n.tone === 'info'));
  protected readonly failures = computed(() => this.notices.all().filter((n) => n.tone === 'failure'));
}
