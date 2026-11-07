import { OverlayContainer } from '@angular/cdk/overlay';
import { TestBed } from '@angular/core/testing';
import { LandmarkOverlayContainer } from './landmark-overlay-container';

describe('LandmarkOverlayContainer', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [{ provide: OverlayContainer, useClass: LandmarkOverlayContainer }] });
  });

  it('puts menus and dialogs in a landmark of their own, at the end of the page', () => {
    const container = TestBed.inject(OverlayContainer).getContainerElement();

    expect(container.getAttribute('role')).toBe('region');
    expect(container.getAttribute('aria-label')).toBe('Menus and dialogs');
    // Where a modal dialog can hide everything else: the CDK hides the container's siblings
    expect(container.parentElement).toBe(document.body);
  });
});
