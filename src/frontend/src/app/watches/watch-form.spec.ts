import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { answer, nextRequest, request, respond, textOf, watchSummary } from '../testing/fixtures';
import { WatchForm } from './watch-form';
import { Watch, WatchScheduleKind } from './watch.model';

@Component({ template: '' })
class WatchPage {}

const savedWatch: Watch = {
  id: 7,
  name: 'Prices',
  scheduleEnabled: true,
  scheduleKind: WatchScheduleKind.Cron,
  scheduleInterval: '0 9 * * 1-5',
  useVpn: true,
  scriptFileName: 'prices.ts',
  scriptFileId: 'a1b2',
};

describe('WatchForm', () => {
  let page: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'watches/new', component: WatchForm },
            { path: 'watches/:id/edit', component: WatchForm },
            { path: 'watches/:id', component: WatchPage },
          ],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function open(url: string) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, WatchForm);
    page = harness.routeNativeElement!;
  }

  const stable = () => TestBed.inject(ApplicationRef).whenStable();

  /** Sets a field as the browser does when the Owner types or picks, which fires both events. */
  function type(selector: string, value: string) {
    const input = page.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
  }

  function chooseScript(file: File) {
    const picker = page.querySelector<HTMLInputElement>('#script')!;
    Object.defineProperty(picker, 'files', { value: [file], configurable: true });
    picker.dispatchEvent(new Event('change'));
  }

  async function submitForm() {
    page.querySelector('form')!.dispatchEvent(new Event('submit'));
    await stable();
  }

  describe('a new watch', () => {
    beforeEach(() => open('/watches/new'));

    it('is created on an interval, as a kind and a count, with its script', async () => {
      type('#name', ' Stock ');
      page.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
      await stable();
      type('#every', '2');
      type('#unit', String(WatchScheduleKind.Hourly));
      const script = new File(['export default class {}'], 'stock.ts');
      chooseScript(script);
      await stable();
      expect(textOf(page.querySelector('app-script-picker'))).toContain('stock.ts');

      await submitForm();
      const create = await nextRequest(request('POST', '/api/Watches'));
      const body = create.request.body as FormData;
      expect(Object.fromEntries(body.entries())).toEqual({
        name: 'Stock',
        scheduleEnabled: 'true',
        scheduleKind: String(WatchScheduleKind.Hourly),
        scheduleInterval: '2',
        useVpn: 'false',
        script,
      });
      create.flush({ ...savedWatch, id: 8, name: 'Stock' });

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/watches/8'));
    });

    it('is created on a cron schedule', async () => {
      type('#name', 'Weekday mornings');
      page.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
      await stable();
      page.querySelector<HTMLInputElement>('input[value=cron]')!.click();
      await stable();
      type('#cron', '0 9 * * 1-5');
      chooseScript(new File([''], 'morning.ts'));

      await submitForm();
      const create = await nextRequest(request('POST', '/api/Watches'));
      const body = create.request.body as FormData;
      expect(body.get('scheduleKind')).toBe(String(WatchScheduleKind.Cron));
      expect(body.get('scheduleInterval')).toBe('0 9 * * 1-5');
      create.flush({ ...savedWatch, id: 9 });

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/watches/9'));
    });

    it('needs a name and a script', async () => {
      await submitForm();

      expect(textOf(page.querySelector('#name-error'))).toBe('Give the watch a name.');
      expect(textOf(page.querySelector('#script-error'))).toBe('Choose a script for the watch to run.');
      TestBed.inject(HttpTestingController).expectNone(request('POST', '/api/Watches'));
    });

    it('needs a cron expression to run on one', async () => {
      type('#name', 'Weekday mornings');
      page.querySelector<HTMLInputElement>('input[type=checkbox]')!.click();
      await stable();
      page.querySelector<HTMLInputElement>('input[value=cron]')!.click();
      chooseScript(new File([''], 'morning.ts'));

      await submitForm();

      expect(textOf(page.querySelector('#cron-error'))).toBe('Enter a cron expression, such as 0 9 * * 1-5.');
    });

    it("lists the Backend's validation problems", async () => {
      type('#name', 'Stock');
      chooseScript(new File([''], 'stock.ts'));

      await submitForm();
      (await nextRequest(request('POST', '/api/Watches'))).flush(
        { errors: { ScheduleInterval: ['ScheduleInterval must be a valid cron expression for Cron schedule.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      await vi.waitFor(async () => {
        await stable();
        expect(textOf(page.querySelector('[role=alert]'))).toBe(
          "Couldn't save: ScheduleInterval must be a valid cron expression for Cron schedule.",
        );
      });
    });
  });

  describe('an existing watch', () => {
    beforeEach(async () => {
      await open('/watches/7/edit');
      await answer(request('GET', '/api/Watches/7'), watchSummary({ ...savedWatch, recentRuns: [] }));
    });

    it('shows its saved cron schedule, and saves it unchanged', async () => {
      expect(textOf(page.querySelector('h1'))).toBe('Edit Prices');
      expect(page.querySelector<HTMLInputElement>('#cron')?.value).toBe('0 9 * * 1-5');
      expect(textOf(page.querySelector('#script-hint'))).toContain('prices.ts');

      await submitForm();
      const update = await nextRequest(request('PUT', '/api/Watches/7'));
      expect(update.request.body).toEqual({
        name: 'Prices',
        scheduleEnabled: true,
        scheduleKind: WatchScheduleKind.Cron,
        scheduleInterval: '0 9 * * 1-5',
        useVpn: true,
      });
      update.flush(savedWatch);

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/watches/7'));
    });

    it('replaces its script when another is chosen', async () => {
      const script = new File(['export default class {}'], 'prices-v2.ts');
      chooseScript(script);

      await submitForm();
      await respond(request('PUT', '/api/Watches/7'), savedWatch);
      const upload = await nextRequest(request('POST', '/api/Watches/7/script'));
      expect((upload.request.body as FormData).get('script')).toBe(script);
      upload.flush('');

      await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/watches/7'));
    });
  });
});
