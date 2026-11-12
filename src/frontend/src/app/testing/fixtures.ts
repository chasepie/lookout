import { HttpRequest } from '@angular/common/http';
import { ApplicationRef } from '@angular/core';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WatchRun, WatchRunStatus, WatchRunSummary, WatchScheduleKind, WatchSummary } from '../watches/watch.model';

/** A run that finished having seen something, with any fields the test cares about overridden. */
export function runSummary(id: number, overrides: Partial<WatchRunSummary> = {}): WatchRunSummary {
  return {
    id,
    watchId: 1,
    createdTime: '2026-11-07T14:00:00Z',
    startTime: '2026-11-07T14:00:00.400Z',
    endTime: '2026-11-07T14:00:06.600Z',
    status: WatchRunStatus.Done,
    content: `seen by run ${id}`,
    failureReason: null,
    askedForFailover: false,
    error: null,
    region: null,
    retryOfRunId: null,
    hasScreenshot: false,
    ...overrides,
  };
}

/** A single run, as `GET .../runs/{id}` returns it. */
export function fullRun(id: number, overrides: Partial<WatchRun> = {}): WatchRun {
  const { hasScreenshot, ...summary } = runSummary(id);
  return {
    ...summary,
    screenshot: hasScreenshot ? 'iVBORw0KGgo=' : null,
    screenshotMediaType: hasScreenshot ? 'image/png' : null,
    ...overrides,
  };
}

export function watchSummary(overrides: Partial<WatchSummary> = {}): WatchSummary {
  return {
    id: 1,
    name: 'Bot detection',
    scheduleEnabled: true,
    scheduleKind: WatchScheduleKind.Minutely,
    scheduleInterval: '15',
    useVpn: true,
    screenshotOnSuccess: false,
    screenshotOnKnownFailure: false,
    screenshotOnCrash: false,
    knownFailureThreshold: 3,
    scriptFileName: 'bot-detection.ts',
    recentRuns: [runSummary(2, { content: 'Normal' }), runSummary(1, { content: 'Normal' })],
    nextRunTime: null,
    ...overrides,
  };
}

export type RequestMatch = (request: HttpRequest<unknown>) => boolean;

/** Matches a request by method and URL, ignoring query parameters. */
export function request(method: string, url: string): RequestMatch {
  return (r) => r.method === method && r.url === url;
}

/**
 * Waits for a request the page is about to send, which may be after change detection or a burst of live updates
 * settling, and returns it to answer.
 */
export function nextRequest(match: RequestMatch): Promise<TestRequest> {
  const backend = TestBed.inject(HttpTestingController);
  return vi.waitFor(() => {
    TestBed.tick();
    return backend.expectOne(match);
  });
}

/**
 * Answers the next matching request without waiting for the page to settle, for when it has other requests out:
 * the page only settles once every request is answered.
 */
export async function respond(match: RequestMatch, body: Parameters<TestRequest['flush']>[0]) {
  (await nextRequest(match)).flush(body);
}

/** Answers the next matching request, which must be the last one out, then lets the page render the answer. */
export async function answer(match: RequestMatch, body: Parameters<TestRequest['flush']>[0]) {
  (await nextRequest(match)).flush(body);
  await TestBed.inject(ApplicationRef).whenStable();
}

/** Fails the next matching request, which must be the last one out, then lets the page render it. */
export async function fail(match: RequestMatch, status: number, body: Parameters<TestRequest['flush']>[0] = '') {
  (await nextRequest(match)).flush(body, { status, statusText: 'Error' });
  await TestBed.inject(ApplicationRef).whenStable();
}

/** The page's text with runs of whitespace collapsed, for asserting on what the Owner reads. */
export function textOf(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}
