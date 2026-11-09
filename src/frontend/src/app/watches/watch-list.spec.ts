import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NoticeStrip } from '../shared/notice-strip';
import { liveUpdates, provideFakeLiveUpdates } from '../testing/fake-live-updates';
import { answer, fail, fullRun, nextRequest, request, runSummary, textOf, watchSummary } from '../testing/fixtures';
import { WatchList } from './watch-list';
import { WatchRunStatus } from './watch.model';

describe('WatchList', () => {
  let harness: RouterTestingHarness;
  let page: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '', component: WatchList }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeLiveUpdates(),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/', WatchList);
    page = harness.routeNativeElement!;
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const rows = () => [...page.querySelectorAll('app-watch-row')];

  it('shows each watch with its schedule and what it last saw', async () => {
    expect(textOf(page)).toContain('Loading watches');

    await answer(request('GET', '/api/Watches'), [
      watchSummary(),
      watchSummary({
        id: 2,
        name: 'Price',
        scheduleEnabled: false,
        useVpn: false,
        recentRuns: [runSummary(5, { watchId: 2, content: null, error: 'Timeout\n  at page.goto' })],
      }),
    ]);

    expect(rows()).toHaveLength(2);
    expect(textOf(rows()[0])).toContain('Every 15 minutes');
    expect(textOf(rows()[0])).toContain('VPN');
    expect(rows()[0].querySelector('.observation')?.textContent).toBe('Normal');
    expect(rows()[0].querySelector('a')?.getAttribute('href')).toBe('/watches/1');
    expect(textOf(rows()[1])).toContain('Not scheduled');
    // A failure shows its first line only
    expect(textOf(rows()[1].querySelector('.failure'))).toBe('Crashed Timeout');
  });

  it('says when a run is under way, while showing what the one before saw', async () => {
    await answer(request('GET', '/api/Watches'), [
      watchSummary({
        recentRuns: [
          runSummary(3, { status: WatchRunStatus.Running, content: null, endTime: null }),
          runSummary(2, { content: 'Normal' }),
        ],
      }),
    ]);

    expect(textOf(rows()[0])).toContain('Running now');
    expect(rows()[0].querySelector('.observation')?.textContent).toBe('Normal');
  });

  it("can't run a watch without a script", async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary({ scriptFileName: null, recentRuns: [] })]);

    expect(textOf(rows()[0])).toContain("Hasn't run yet");
    expect(rows()[0].querySelector<HTMLButtonElement>('button')?.disabled).toBe(true);
  });

  it('invites the Owner to make the first watch', async () => {
    await answer(request('GET', '/api/Watches'), []);

    expect(textOf(page)).toContain('Nothing to watch yet');
    expect(page.querySelector('a[href="/watches/new"]')).not.toBeNull();
  });

  it('says why the watches could not be loaded, and tries again', async () => {
    await fail(request('GET', '/api/Watches'), 500, 'Database is locked');

    expect(textOf(page.querySelector('[role=alert] p'))).toBe("Couldn't load the watches: Database is locked");
    page.querySelector<HTMLButtonElement>('[role=alert] button')!.click();
    await answer(request('GET', '/api/Watches'), [watchSummary()]);
    expect(rows()).toHaveLength(1);
  });

  it('refreshes only the row whose run changed', async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary(), watchSummary({ id: 2, name: 'Price' })]);

    liveUpdates().runUpdated.next({ ...runSummary(3), status: WatchRunStatus.Running });
    const row = await nextRequest(request('GET', '/api/Watches/1'));
    expect(row.request.params.get('recentRuns')).toBe('24');
    row.flush(watchSummary({ name: 'Bot check' }));

    // The row is put in place once its answer is read
    await vi.waitFor(async () => {
      await TestBed.inject(ApplicationRef).whenStable();
      expect(textOf(rows()[0])).toContain('Bot check');
    });
    expect(textOf(rows()[1])).toContain('Price');
    TestBed.inject(HttpTestingController).expectNone(request('GET', '/api/Watches'));
  });

  it('takes out the row of a watch that has gone', async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary(), watchSummary({ id: 2, name: 'Price' })]);

    liveUpdates().runsDeleted.next({ watchId: 2, runIds: [9] });
    await fail(request('GET', '/api/Watches/2'), 404);

    await vi.waitFor(async () => {
      await TestBed.inject(ApplicationRef).whenStable();
      expect(rows()).toHaveLength(1);
    });
  });

  it('reloads every row when a watch changes', async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary()]);

    liveUpdates().watchChanged.next(5);
    await answer(request('GET', '/api/Watches'), [watchSummary(), watchSummary({ id: 5, name: 'New one' })]);

    expect(rows()).toHaveLength(2);
  });

  it('keeps the watches shown when a refresh fails, and says so', async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary()]);

    liveUpdates().reconnected.next();
    await fail(request('GET', '/api/Watches'), 502, 'Bad Gateway');

    expect(rows()).toHaveLength(1);
    expect(textOf(page.querySelector('[role=alert] p'))).toBe("Couldn't refresh the watches: Bad Gateway");
  });

  describe('Run now', () => {
    let strip: ComponentFixture<NoticeStrip>;
    let notices: HTMLElement;

    beforeEach(async () => {
      // Notices appear in the shell, so render its strip alongside the page
      strip = TestBed.createComponent(NoticeStrip);
      notices = strip.nativeElement as HTMLElement;
      await answer(request('GET', '/api/Watches'), [watchSummary()]);
    });

    it('starts a run and says so', async () => {
      rows()[0].querySelector<HTMLButtonElement>('button')!.click();
      (await nextRequest(request('POST', '/api/Watches/1/runs'))).flush(fullRun(7, { status: WatchRunStatus.Created }));
      // The new run shows in its row straight away, without waiting for the live connection
      await answer(request('GET', '/api/Watches/1'), watchSummary());
      await strip.whenStable();

      expect(textOf(notices)).toContain('Started run #7 of Bot detection.');
    });

    it('says why a run could not start', async () => {
      rows()[0].querySelector<HTMLButtonElement>('button')!.click();
      await fail(request('POST', '/api/Watches/1/runs'), 400, 'Upload a script before running this watch.');
      await strip.whenStable();

      expect(textOf(notices.querySelector('[role=alert]'))).toContain(
        "Couldn't run Bot detection: Upload a script before running this watch.",
      );
    });
  });
});
