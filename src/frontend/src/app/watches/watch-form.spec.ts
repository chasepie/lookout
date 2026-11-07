import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { WatchForm } from './watch-form';
import { WatchScheduleKind, WatchSummary } from './watch.model';
import { WatchesApi } from './watches-api';

const saved: WatchSummary = {
  id: 7,
  name: 'Prices',
  scheduleEnabled: true,
  scheduleKind: WatchScheduleKind.Cron,
  scheduleInterval: '0 9 * * 1-5',
  useVpn: true,
  scriptFileName: 'prices.ts',
  recentRuns: [],
  nextRunTime: null,
};

describe('WatchForm', () => {
  let api: { create: ReturnType<typeof vi.fn>; update: ReturnType<typeof vi.fn>; watchResource: () => unknown };

  beforeEach(() => {
    api = {
      create: vi.fn().mockResolvedValue({ ...saved, id: 8 }),
      update: vi.fn().mockResolvedValue(saved),
      watchResource: () => ({ hasValue: () => true, value: signal(saved) }),
    };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: WatchesApi, useValue: api }],
    });
  });

  async function render(id?: string) {
    const fixture = TestBed.createComponent(WatchForm);
    if (id) {
      fixture.componentRef.setInput('id', id);
    }
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  /** Sets a field as the browser does when the Owner types or picks, which fires both events. */
  function type(input: HTMLInputElement | HTMLSelectElement, value: string) {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
  }

  it('creates a watch on an interval, as a kind and a count', async () => {
    const { fixture, element } = await render();
    type(element.querySelector<HTMLInputElement>('#name')!, ' Stock ');
    element.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
    await fixture.whenStable();
    type(element.querySelector<HTMLInputElement>('#every')!, '2');
    type(element.querySelector<HTMLSelectElement>('#unit')!, String(WatchScheduleKind.Hourly));
    const script = new File(['export default class {}'], 'stock.ts');
    const picker = element.querySelector<HTMLInputElement>('#script')!;
    Object.defineProperty(picker, 'files', { value: [script] });
    picker.dispatchEvent(new Event('change'));

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await vi.waitFor(() => expect(api.create).toHaveBeenCalled());

    expect(api.create).toHaveBeenCalledWith(
      {
        name: 'Stock',
        scheduleEnabled: true,
        scheduleKind: WatchScheduleKind.Hourly,
        scheduleInterval: '2',
        useVpn: false,
      },
      script,
    );
  });

  it("won't create a watch without a script", async () => {
    const { fixture, element } = await render();
    type(element.querySelector<HTMLInputElement>('#name')!, 'Stock');

    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(api.create).not.toHaveBeenCalled();
    expect(element.querySelector('#script-error')?.textContent).toContain('Choose a script');
  });

  it('shows a saved cron schedule as one, and saves it unchanged', async () => {
    const { element } = await render('7');

    expect(element.querySelector<HTMLInputElement>('#cron')?.value).toBe('0 9 * * 1-5');
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await vi.waitFor(() => expect(api.update).toHaveBeenCalled());

    expect(api.update).toHaveBeenCalledWith('7', {
      name: 'Prices',
      scheduleEnabled: true,
      scheduleKind: WatchScheduleKind.Cron,
      scheduleInterval: '0 9 * * 1-5',
      useVpn: true,
    });
  });
});
