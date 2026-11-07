import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { BreakpointObserver } from '@angular/cdk/layout';
import { Overlay } from '@angular/cdk/overlay';
import { Component, Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { phoneQuery } from './breakpoints';

export interface ConfirmOptions {
  /** The question, such as "Delete Bot detection?". */
  title: string;
  /** What will happen, so the answer is an informed one. */
  body?: string;
  /** Names the action, such as "Delete watch"; the other button is always "Cancel". */
  confirmLabel: string;
  /** Whether the action destroys something, which colours its button. */
  destructive?: boolean;
  /**
   * Where focus goes when the dialog closes, if not where it was when it opened: needed when that was a menu item,
   * which is gone by then.
   */
  returnFocusTo?: HTMLElement;
}

/** What the dialog is told: the question, and whether to rise from the bottom as a sheet on a phone. */
interface ConfirmDialogData extends ConfirmOptions {
  sheet: boolean;
}

/** Asks before doing something that can't be undone. On a phone it rises from the bottom, within reach of a thumb. */
@Service()
export class Confirm {
  private readonly dialog = inject(Dialog);
  private readonly overlay = inject(Overlay);
  private readonly breakpoints = inject(BreakpointObserver);

  /** Resolves to whether the Owner chose to go ahead. */
  async ask(options: ConfirmOptions): Promise<boolean> {
    const sheet = this.breakpoints.isMatched(phoneQuery);
    const ref = this.dialog.open<boolean, ConfirmDialogData>(ConfirmDialog, {
      data: { ...options, sheet },
      role: 'alertdialog',
      ariaLabelledBy: 'confirm-title',
      ariaDescribedBy: options.body ? 'confirm-body' : null,
      ...(sheet
        ? {
            panelClass: 'dialog-sheet',
            positionStrategy: this.overlay.position().global().bottom('0').centerHorizontally(),
          }
        : { panelClass: 'dialog-panel' }),
      backdropClass: 'dialog-backdrop',
      // Cancel comes first, so Enter on a destructive dialog doesn't destroy anything by accident
      autoFocus: 'first-tabbable',
      restoreFocus: options.returnFocusTo ?? true,
    });
    return (await firstValueFrom(ref.closed)) === true;
  }
}

@Component({
  selector: 'app-confirm-dialog',
  host: { '[class.sheet]': 'options.sheet' },
  template: `
    <h2 id="confirm-title">{{ options.title }}</h2>
    @if (options.body) {
      <p id="confirm-body">{{ options.body }}</p>
    }
    <div class="actions">
      <button type="button" class="button" (click)="ref.close(false)">Cancel</button>
      <button
        type="button"
        class="button"
        [class.button-danger]="options.destructive"
        [class.button-primary]="!options.destructive"
        (click)="ref.close(true)"
      >
        {{ options.confirmLabel }}
      </button>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
      padding: var(--space-5);
      border-radius: var(--radius-sheet);
      background: var(--paper-raised);
      box-shadow: var(--shadow);
    }

    h2 {
      font-size: var(--step-2);
    }

    .actions {
      justify-content: flex-end;
      margin-top: var(--space-2);
    }

    // A sheet rises from the bottom, its buttons full width: the action on top, Cancel nearest the thumb
    :host(.sheet) {
      border-radius: var(--radius-sheet) var(--radius-sheet) 0 0;
      padding-bottom: calc(var(--space-5) + env(safe-area-inset-bottom));
      animation: sheet-rise 220ms var(--ease-out);

      .actions {
        flex-direction: column-reverse;
        align-items: stretch;
      }
    }

    @keyframes sheet-rise {
      from {
        transform: translateY(100%);
      }
    }
  `,
})
export class ConfirmDialog {
  protected readonly options = inject<ConfirmDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
}
