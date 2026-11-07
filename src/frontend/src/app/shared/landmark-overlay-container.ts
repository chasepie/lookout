import { OverlayContainer } from '@angular/cdk/overlay';
import { Injectable } from '@angular/core';

/**
 * Makes the container menus and dialogs open in a landmark of its own, so their content belongs to one (AXE's
 * `region` rule). It stays at the end of `<body>`, where a modal dialog can hide everything else from assistive
 * technology: the CDK hides the container's siblings, which there are the whole rest of the page.
 */
@Injectable()
export class LandmarkOverlayContainer extends OverlayContainer {
  protected override _createContainer(): void {
    super._createContainer();
    this._containerElement?.setAttribute('role', 'region');
    this._containerElement?.setAttribute('aria-label', 'Menus and dialogs');
  }
}
