import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NoticeStrip } from '../shared/notice-strip';
import { liveUpdates, provideFakeLiveUpdates } from '../testing/fake-live-updates';
import { answer, fail, fullRun, request, runSummary, textOf, watchSummary } from '../testing/fixtures';
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
    expect(textOf(rows()[1].querySelector('.failure'))).toBe('Failed Timeout');
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

  it('says why the watches could not be loaded', async () => {
    await fail(request('GET', '/api/Watches'), 500, 'Database is locked');

    expect(textOf(page.querySelector('[role=alert]'))).toBe("Couldn't load the watches: Database is locked");
  });

  it('reloads when a run changes', async () => {
    await answer(request('GET', '/api/Watches'), [watchSummary()]);

    liveUpdates().runUpdated.next({ ...runSummary(3), status: WatchRunStatus.Running });
    await answer(request('GET', '/api/Watches'), [watchSummary({ name: 'Renamed' })]);

    expect(textOf(rows()[0])).toContain('Renamed');
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
      await answer(request('POST', '/api/Watches/1/runs'), fullRun(7, { status: WatchRunStatus.Created }));
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
