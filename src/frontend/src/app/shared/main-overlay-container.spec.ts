import { OverlayContainer } from '@angular/cdk/overlay';
import { TestBed } from '@angular/core/testing';
import { MainOverlayContainer } from './main-overlay-container';

describe('MainOverlayContainer', () => {
  let main: HTMLElement;

  beforeEach(() => {
    main = document.body.appendChild(document.createElement('main'));
    TestBed.configureTestingModule({ providers: [{ provide: OverlayContainer, useClass: MainOverlayContainer }] });
  });

  afterEach(() => main.remove());

  it('puts menus and dialogs inside <main>, so they belong to a landmark', () => {
    const container = TestBed.inject(OverlayContainer).getContainerElement();

    expect(container.parentElement).toBe(main);
  });
});
