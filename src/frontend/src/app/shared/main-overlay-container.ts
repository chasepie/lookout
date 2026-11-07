import { OverlayContainer } from '@angular/cdk/overlay';
import { Injectable } from '@angular/core';

/**
 * Puts menus and dialogs inside `<main>` instead of at the end of `<body>`, so they belong to a landmark like the
 * rest of the page's content (AXE's `region` rule). They're positioned fixed, so where they sit in the DOM doesn't
 * change where they appear.
 */
@Injectable()
export class MainOverlayContainer extends OverlayContainer {
  protected override _createContainer(): void {
    super._createContainer();
    const main = this._document.querySelector('main');
    if (main && this._containerElement) {
      main.appendChild(this._containerElement);
    }
  }
}
