import { TestBed } from '@angular/core/testing';
import { RunStrip } from './run-strip';
import { WatchRunStatus, WatchRunSummary } from './watch.model';

function run(id: number, outcome: { content?: string; failureReason?: string; error?: string; status?: WatchRunStatus }): WatchRunSummary {
  return {
    id,
    watchId: 1,
    createdTime: '2026-11-07T12:00:00Z',
    startTime: null,
    endTime: null,
    status: outcome.status ?? WatchRunStatus.Done,
    content: outcome.content ?? null,
    failureReason: outcome.failureReason ?? null,
    error: outcome.error ?? null,
    askedForFailover: false,
    region: null,
    retryOfRunId: null,
    hasScreenshot: false,
  };
}

describe('RunStrip', () => {
  async function render(runs: WatchRunSummary[]) {
    const fixture = TestBed.createComponent(RunStrip);
    fixture.componentRef.setInput('runs', runs);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('marks changes, repeats and failures by shape, oldest first', async () => {
    // Newest first, as the API sends them
    const strip = await render([
      run(5, { status: WatchRunStatus.Running }),
      run(4, { failureReason: 'Blocked' }),
      run(3, { error: 'Timeout' }),
      run(2, { content: 'A' }),
      run(1, { content: 'A' }),
    ]);

    const kinds = [...strip.querySelectorAll('rect')].map((rect) => rect.getAttribute('class'));
    expect(kinds).toEqual(['changed', 'same', 'crashed', 'knownFailure', 'running']);
    const titles = [...strip.querySelectorAll('rect title')].map((title) => title.textContent);
    expect(titles[2]).toContain('Crashed: Timeout');
    expect(titles[3]).toContain('Known failure: Blocked');
  });

  it('sums the runs up in words', async () => {
    const strip = await render([run(3, { error: 'Timeout' }), run(2, { content: 'B' }), run(1, { content: 'A' })]);

    expect(strip.querySelector('svg')?.getAttribute('aria-label')).toBe('Last 3 runs, 1 failed, 2 new results');
  });
});
