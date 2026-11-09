import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject, signal } from '@angular/core';
import { LucideX } from '@lucide/angular';

export interface ScreenshotViewerData {
  /** Names the screenshot, such as "Run #7 screenshot". */
  title: string;
  /** The image, as a `data:` URL. */
  src: string;
  /** Its size, such as "1,265 × 5,150 px", once known. */
  size: string | null;
}

/**
 * A run's screenshot filling the window, as wide as the window allows up to its own size, so a full-page screenshot
 * reads at about the size the page was, scrolling down. Opened by the run panel, whose preview is cropped and small.
 */
@Component({
  selector: 'app-screenshot-viewer',
  imports: [LucideX],
  template: `
    <header class="head">
      <h2 id="screenshot-viewer-title">{{ data.title }}</h2>
      @if (data.size; as size) {
        <span class="size">{{ size }}</span>
      }
      <button
        type="button"
        class="button button-quiet actual-size"
        [attr.aria-pressed]="actualSize()"
        (click)="actualSize.set(!actualSize())"
      >
        Actual size
      </button>
      <button type="button" class="button button-quiet" (click)="ref.close()">
        <svg lucideX aria-hidden="true" />
        Close
      </button>
    </header>
    <div class="scroll" [class.scroll-actual]="actualSize()" tabindex="0" aria-label="Screenshot, scrollable">
      <img [src]="data.src" alt="The run's screenshot" />
    </div>
  `,
  styles: `
    @use 'breakpoints' as bp;

    :host {
      display: grid;
      grid-template-rows: auto minmax(0, 1fr);
      width: 100%;
      height: 100%;
      background: var(--paper);
    }

    .head {
      display: flex;
      align-items: center;
      gap: var(--space-4);
      padding: var(--space-2) var(--space-2) var(--space-2) var(--gutter);
      border-bottom: 1px solid var(--rule);
      background: var(--paper-raised);
    }

    h2 {
      font-size: var(--step-1);
      white-space: nowrap;
    }

    .size {
      color: var(--ink-muted);
      font-size: var(--step--1);
      white-space: nowrap;

      @include bp.phone {
        display: none;
      }
    }

    .actual-size {
      margin-left: auto;

      &[aria-pressed='true'] {
        background: var(--paper-sunk);
        box-shadow: inset 0 0 0 1px var(--deep);
      }
    }

    .scroll {
      overflow: auto;
      padding: var(--space-4) var(--gutter);
      outline-offset: -2px;
    }

    img {
      display: block;
      max-width: 100%;
      height: auto;
      margin-inline: auto;
      border: 1px solid var(--rule);
      background: var(--paper-sunk);
    }

    // At its own size, as on a phone, where fitting a full page to the width makes it too small to read
    .scroll-actual img {
      max-width: none;
    }
  `,
})
export class ScreenshotViewer {
  protected readonly data = inject<ScreenshotViewerData>(DIALOG_DATA);
  protected readonly ref = inject(DialogRef);
  /** Whether the screenshot is shown at its own size, scrolling both ways, rather than fitted to the window's width. */
  protected readonly actualSize = signal(false);
}
