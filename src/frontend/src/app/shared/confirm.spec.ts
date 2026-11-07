import { TestBed } from '@angular/core/testing';
import { Confirm } from './confirm';

/** Makes every media query match, or none, as the CDK reads them through `matchMedia`. */
function screenIsPhone(phone: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: phone,
    media: query,
    addListener: () => undefined,
    removeListener: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

describe('Confirm', () => {
  afterEach(() => vi.unstubAllGlobals());

  /** Opens the dialog and renders it, returning the answer to come. */
  function ask(): Promise<boolean> {
    const answer = TestBed.inject(Confirm).ask({ title: 'Delete it?', confirmLabel: 'Delete', destructive: true });
    TestBed.tick();
    return answer;
  }

  const dialogButton = (name: string) =>
    [...document.querySelectorAll<HTMLButtonElement>('[role=alertdialog] button')].find(
      (button) => button.textContent?.trim() === name,
    )!;

  it('goes ahead only when the Owner confirms', async () => {
    screenIsPhone(false);
    const answer = ask();

    dialogButton('Delete').click();

    expect(await answer).toBe(true);
  });

  it('does nothing when the Owner cancels', async () => {
    screenIsPhone(false);
    const answer = ask();

    dialogButton('Cancel').click();

    expect(await answer).toBe(false);
  });

  it('asks in the middle of a large screen', () => {
    screenIsPhone(false);
    void ask();

    expect(document.querySelector('.cdk-overlay-pane')?.classList).toContain('dialog-panel');
    dialogButton('Cancel').click();
  });

  it('rises from the bottom of a phone, within reach of a thumb', () => {
    screenIsPhone(true);
    void ask();

    expect(document.querySelector('.cdk-overlay-pane')?.classList).toContain('dialog-sheet');
    expect(document.querySelector('app-confirm-dialog')?.classList).toContain('sheet');
    dialogButton('Cancel').click();
  });
});
