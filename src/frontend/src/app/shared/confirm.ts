import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Service, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

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

/** Asks before doing something that can't be undone. */
@Service()
export class Confirm {
  private readonly dialog = inject(Dialog);

  /** Resolves to whether the Owner chose to go ahead. */
  async ask(options: ConfirmOptions): Promise<boolean> {
    const ref = this.dialog.open<boolean, ConfirmOptions>(ConfirmDialog, {
      data: options,
      role: 'alertdialog',
      ariaLabelledBy: 'confirm-title',
      ariaDescribedBy: options.body ? 'confirm-body' : null,
      panelClass: 'dialog-panel',
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
  `,
})
export class ConfirmDialog {
  protected readonly options = inject<ConfirmOptions>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);
}
