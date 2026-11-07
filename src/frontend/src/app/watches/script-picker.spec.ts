import { TestBed } from '@angular/core/testing';
import { textOf } from '../testing/fixtures';
import { ScriptPicker } from './script-picker';

describe('ScriptPicker', () => {
  async function render() {
    const fixture = TestBed.createComponent(ScriptPicker);
    fixture.componentRef.setInput('inputId', 'script');
    await fixture.whenStable();
    return fixture;
  }

  /** A drag event carrying `files`, which jsdom can't build itself. */
  function dragEvent(type: string, files: File[] = []) {
    const event = new Event(type, { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: { files } });
    return event;
  }

  it('takes a script dropped on it', async () => {
    const fixture = await render();
    const zone = (fixture.nativeElement as HTMLElement).querySelector('label')!;
    const script = new File(['export default class {}'], 'dropped.ts');

    const over = dragEvent('dragover');
    zone.dispatchEvent(over);
    await fixture.whenStable();
    expect(over.defaultPrevented).toBe(true);
    expect(zone.classList).toContain('zone-over');

    zone.dispatchEvent(dragEvent('drop', [script]));
    await fixture.whenStable();

    expect(fixture.componentInstance.file()).toBe(script);
    expect(zone.classList).not.toContain('zone-over');
    expect(textOf(zone)).toContain('dropped.ts');
  });

  it('stops showing the drop target when a drag leaves', async () => {
    const fixture = await render();
    const zone = (fixture.nativeElement as HTMLElement).querySelector('label')!;

    zone.dispatchEvent(dragEvent('dragover'));
    zone.dispatchEvent(dragEvent('dragleave'));
    await fixture.whenStable();

    expect(zone.classList).not.toContain('zone-over');
  });

  it("turns away a file that isn't a script", async () => {
    const fixture = await render();
    const element = fixture.nativeElement as HTMLElement;

    element.querySelector('label')!.dispatchEvent(dragEvent('drop', [new File([''], 'screenshot.png')]));
    await fixture.whenStable();

    expect(fixture.componentInstance.file()).toBeNull();
    expect(element.querySelector('[role=alert]')?.textContent).toContain(
      "screenshot.png isn't a script. Choose a .ts or .js file.",
    );
  });
});
