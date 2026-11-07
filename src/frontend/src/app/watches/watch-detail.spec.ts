import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NoticeStrip } from '../shared/notice-strip';
import { liveUpdates, provideFakeLiveUpdates } from '../testing/fake-live-updates';
import { answer, fail, fullRun, nextRequest, request, respond, runSummary, textOf, watchSummary } from '../testing/fixtures';
import { RunPanel } from './run-panel';
import { WatchDetail } from './watch-detail';
import { WatchRunStatus } from './watch.model';

@Component({ template: '' })
class Home {}

/** Minutes before now, as the API writes times, so the log files the run under today. */
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

const watchUrl = '/api/Watches/1';
const runsUrl = '/api/Watches/1/runs';

describe('WatchDetail', () => {
  let harness: RouterTestingHarness;
  let strip: ComponentFixture<NoticeStrip>;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: '', component: Home },
            { path: 'watches/:id', component: WatchDetail, children: [{ path: 'runs/:runId', component: RunPanel }] },
          ],
          withComponentInputBinding(),
          withRouterConfig({ paramsInheritanceStrategy: 'always' }),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeLiveUpdates(),
      ],
    });
    strip = TestBed.createComponent(NoticeStrip);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const page = () => harness.routeNativeElement!;
  const stable = async () => {
    await TestBed.inject(ApplicationRef).whenStable();
    await strip.whenStable();
  };
  const notices = () => textOf(strip.nativeElement as HTMLElement);
  const entries = () => [...page().querySelectorAll('.entry')];
  const button = (name: string) =>
    [...page().querySelectorAll<HTMLButtonElement>('button')].find((b) => textOf(b) === name)!;

  const log = [
    runSummary(3, { createdTime: minutesAgo(1), content: 'Normal', hasScreenshot: true }),
    runSummary(2, { createdTime: minutesAgo(2), content: 'Normal' }),
    runSummary(1, { createdTime: minutesAgo(3), content: 'Robot' }),
  ];

  /** Opens a page of the watch, answering its requests for the watch and its log. A run's is left to the test. */
  async function open(url = '/watches/1') {
    await harness.navigateByUrl(url);
    const nextRunTime = new Date(Date.now() + 12 * 60_000).toISOString();
    await respond(request('GET', watchUrl), watchSummary({ nextRunTime }));
    if (url.includes('/runs/')) {
      await respond(request('GET', runsUrl), log);
    } else {
      await answer(request('GET', runsUrl), log);
    }
  }

  /** Picks an item from the "More actions" menu, then answers the dialog that asks first. */
  async function fromMenu(item: string, confirm: boolean) {
    button('More actions').click();
    await stable();
    [...document.querySelectorAll<HTMLButtonElement>('[role=menuitem]')].find((b) => textOf(b) === item)!.click();
    await stable();
    const dialog = document.querySelector('[role=alertdialog]')!;
    const choice = [...dialog.querySelectorAll<HTMLButtonElement>('button')];
    (confirm ? choice[1] : choice[0]).click();
    await stable();
  }

  it("shows the watch's settings and its log, grouped by day, with ditto marks for repeats", async () => {
    await open();

    expect(textOf(page().querySelector('h1'))).toBe('Bot detection');
    const facts = textOf(page().querySelector('.facts'));
    expect(facts).toContain('Every 15 minutes');
    expect(facts).toContain('in 12 min');
    expect(facts).toContain('Through the VPN');
    expect(facts).toContain('bot-detection.ts');

    const dayHead = page().querySelectorAll('.day-head > span');
    expect([...dayHead].map(textOf)).toEqual(['Today', '3 runs']);
    expect(textOf(entries()[0])).toContain('Normal');
    expect(textOf(entries()[1])).toContain('Same as above: Normal');
    expect(entries()[1].querySelector('.observation')).toBeNull();
    expect(textOf(entries()[2])).toContain('Robot');
    expect(textOf(entries()[0])).toContain('Has a screenshot');
    expect(entries()[0].getAttribute('href')).toBe('/watches/1/runs/3');
  });

  it("says so when there's no such watch", async () => {
    await harness.navigateByUrl('/watches/1');
    (await nextRequest(request('GET', watchUrl))).flush('', { status: 404, statusText: 'Not Found' });
    await fail(request('GET', runsUrl), 404);

    expect(textOf(page().querySelector('h1'))).toBe('No such watch');
  });

  it('starts a run', async () => {
    await open();

    button('Run now').click();
    await answer(request('POST', runsUrl), fullRun(4, { status: WatchRunStatus.Created }));
    await stable();

    expect(notices()).toContain('Started run #4.');
  });

  it('reloads the watch and its log as runs change', async () => {
    await open();

    liveUpdates().runUpdated.next({ ...runSummary(4), status: WatchRunStatus.Running });
    await respond(request('GET', watchUrl), watchSummary({ name: 'Bot check' }));
    await answer(request('GET', runsUrl), [runSummary(4, { createdTime: minutesAgo(0), content: 'Robot' }), ...log]);

    expect(textOf(page().querySelector('h1'))).toBe('Bot check');
    expect(entries()).toHaveLength(4);
    expect(entries()[0].classList).toContain('entry-arrived');
  });

  it('deletes the watch once the Owner confirms', async () => {
    await open();

    await fromMenu('Delete watch', true);
    await answer(request('DELETE', watchUrl), watchSummary());
    await stable();

    expect(TestBed.inject(Router).url).toBe('/');
    expect(notices()).toContain('Deleted Bot detection.');
  });

  it('keeps the watch when the Owner cancels, and gives focus back to the menu', async () => {
    await open();

    await fromMenu('Delete watch', false);

    TestBed.inject(HttpTestingController).expectNone(request('DELETE', watchUrl));
    expect(document.activeElement?.textContent).toContain('More actions');
  });

  it('deletes every run, keeping any in progress', async () => {
    await open();

    await fromMenu('Delete every run', true);
    await answer(request('DELETE', runsUrl), { deleted: 3, keptRunning: 1 });
    await stable();

    expect(notices()).toContain('Deleted 3 runs. Kept 1 in progress.');
  });

  describe('an open run', () => {
    const panel = () => page().querySelector('app-run-panel')!;

    it('shows what it saw, how long it took and its screenshot, beside the log', async () => {
      await open('/watches/1/runs/3');
      await answer(request('GET', `${runsUrl}/3`), fullRun(3, { content: 'Normal', screenshot: 'iVBORw0KGgo=' }));

      expect(textOf(panel().querySelector('h2'))).toBe('Run #3');
      expect(textOf(panel().querySelector('.seen'))).toBe('Normal');
      expect([...panel().querySelectorAll('.timings dd')].map(textOf)).toEqual(['0.4 s', '6.2 s']);
      expect(panel().querySelector('img')?.getAttribute('src')).toBe('data:image/png;base64,iVBORw0KGgo=');
      expect(entries()[0].getAttribute('aria-current')).toBe('true');
      expect(document.activeElement).toBe(panel().querySelector('h2'));

      const actualSize = [...panel().querySelectorAll('button')].find((b) => textOf(b) === 'Actual size')!;
      actualSize.click();
      await stable();
      expect(actualSize.getAttribute('aria-pressed')).toBe('true');
    });

    it("shows a failure's first line as the outcome and all of it under Error", async () => {
      await open('/watches/1/runs/3');
      await answer(request('GET', `${runsUrl}/3`), fullRun(3, { content: null, error: 'Timeout\n  at page.goto' }));

      expect(textOf(panel().querySelector('.seen'))).toBe('Failed Timeout');
      expect(panel().querySelector('pre')?.textContent).toBe('Timeout\n  at page.goto');
    });

    it("can't be deleted while it's running", async () => {
      await open('/watches/1/runs/3');
      await answer(request('GET', `${runsUrl}/3`), fullRun(3, { status: WatchRunStatus.Running, endTime: null }));

      expect(textOf(panel().querySelector('.seen'))).toBe('Running');
      expect(textOf(panel().querySelector('.timings'))).toContain('Still running');
      expect(button('Delete run').disabled).toBe(true);
    });

    it('is deleted once the Owner confirms, closing the panel', async () => {
      await open('/watches/1/runs/3');
      await answer(request('GET', `${runsUrl}/3`), fullRun(3));

      button('Delete run').click();
      await stable();
      [...document.querySelectorAll<HTMLButtonElement>('[role=alertdialog] button')][1].click();
      await answer(request('DELETE', `${runsUrl}/3`), fullRun(3));
      await stable();

      expect(TestBed.inject(Router).url).toBe('/watches/1');
      expect(notices()).toContain('Deleted run #3.');
    });

    it("says so when it's been deleted", async () => {
      await open('/watches/1/runs/9');
      await fail(request('GET', `${runsUrl}/9`), 404);

      expect(textOf(panel())).toContain('This run was deleted.');
    });
  });
});
