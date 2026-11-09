import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { answer, fail, nextRequest, request, textOf } from '../testing/fixtures';
import { NotificationChannelSummary, NotificationService } from './notification.model';
import { NotificationsPage } from './notifications-page';

const channelsUrl = '/api/NotificationChannels';

function channel(overrides: Partial<NotificationChannelSummary> = {}): NotificationChannelSummary {
  return {
    id: 1,
    name: 'Phone',
    service: NotificationService.Pushover,
    pushover: { retrySeconds: 60, expireSeconds: 3600 },
    watches: [],
    ...overrides,
  };
}

describe('NotificationsPage', () => {
  let page: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'notifications', component: NotificationsPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/notifications', NotificationsPage);
    page = harness.routeNativeElement!;
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const stable = () => TestBed.inject(ApplicationRef).whenStable();
  const button = (name: string, within: ParentNode = page) =>
    [...within.querySelectorAll<HTMLButtonElement>('button')].find((b) => textOf(b) === name)!;

  /** Sets a field as the browser does when the Owner types, which fires both events. */
  function type(selector: string, value: string) {
    const input = page.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
  }

  async function submitForm() {
    page.querySelector('form')!.dispatchEvent(new Event('submit'));
    await stable();
  }

  it('lists each channel with its service and the watches that use it', async () => {
    await answer(request('GET', channelsUrl), [
      channel({ watches: [{ id: 4, name: 'Price' }, { id: 3, name: 'Shoe stock' }] }),
      channel({ id: 2, name: 'Tablet', pushover: { retrySeconds: 30, expireSeconds: 10800 } }),
    ]);

    const cards = [...page.querySelectorAll('.channel')];
    expect(cards.map((c) => textOf(c.querySelector('h2')))).toEqual(['Phone', 'Tablet']);
    expect(textOf(cards[0])).toContain('Pushover');
    expect(textOf(cards[0])).toContain('Used by Price and Shoe stock');
    expect(textOf(cards[0])).toContain('Emergencies repeat every 1 min for 1 h');
    expect(textOf(cards[1])).toContain('No watch uses it yet');
    expect(textOf(cards[1])).toContain('Emergencies repeat every 30 s for 3 h');
  });

  it('adds a Pushover channel with its credentials', async () => {
    await answer(request('GET', channelsUrl), []);
    expect(textOf(page)).toContain('No channels yet');

    button('Add a channel').click();
    await stable();
    type('#channel-name', ' Phone ');
    type('#application-token', ' azGDORePK8gMaC0QOYAMyEEuzJnyUi ');
    type('#user-key', 'uQiRzpo4DXghDmr9QzzfQu27cmVRsG');
    type('#retry', '45');
    type('#expire', '1800');
    await submitForm();

    const create = await nextRequest(request('POST', channelsUrl));
    expect(create.request.body).toEqual({
      name: 'Phone',
      service: NotificationService.Pushover,
      pushover: {
        applicationToken: 'azGDORePK8gMaC0QOYAMyEEuzJnyUi',
        userKey: 'uQiRzpo4DXghDmr9QzzfQu27cmVRsG',
        retrySeconds: 45,
        expireSeconds: 1800,
      },
    });
    create.flush(channel());
    await answer(request('GET', channelsUrl), [channel()]);

    expect(page.querySelector('form')).toBeNull();
    expect([...page.querySelectorAll('.channel h2')].map(textOf)).toEqual(['Phone']);
  });

  it("won't add a channel without its credentials", async () => {
    await answer(request('GET', channelsUrl), []);
    button('Add a channel').click();
    await stable();

    type('#channel-name', 'Phone');
    await submitForm();

    expect(textOf(page.querySelector('#application-token-error'))).toBe("Enter the Pushover application's API token.");
    expect(textOf(page.querySelector('#user-key-error'))).toBe('Enter your Pushover user key, or a group key.');
    TestBed.inject(HttpTestingController).expectNone(request('POST', channelsUrl));
  });

  it("edits a channel, keeping its credentials when they're left blank", async () => {
    await answer(request('GET', channelsUrl), [channel()]);

    button('Edit', page.querySelector('.channel')!).click();
    await stable();
    expect(page.querySelector<HTMLInputElement>('#channel-name')!.value).toBe('Phone');
    expect(page.querySelector<HTMLInputElement>('#application-token')!.value).toBe('');
    type('#channel-name', 'Phone, quietly');
    type('#retry', '120');
    await submitForm();

    const update = await nextRequest(request('PUT', `${channelsUrl}/1`));
    expect(update.request.body).toEqual({
      name: 'Phone, quietly',
      service: NotificationService.Pushover,
      pushover: { applicationToken: null, userKey: null, retrySeconds: 120, expireSeconds: 3600 },
    });
    update.flush(channel({ name: 'Phone, quietly', pushover: { retrySeconds: 120, expireSeconds: 3600 } }));
    await answer(request('GET', channelsUrl), [channel({ name: 'Phone, quietly' })]);

    expect(page.querySelector('form')).toBeNull();
    expect(textOf(page.querySelector('.channel h2'))).toBe('Phone, quietly');
  });

  it('sends a test notification and says it went', async () => {
    await answer(request('GET', channelsUrl), [channel()]);

    button('Send a test', page.querySelector('.channel')!).click();
    await answer(request('POST', `${channelsUrl}/1/test`), '');
    // The page shows the outcome once the request's promise settles, after the response
    await stable();

    expect(textOf(page.querySelector('.channel [role=status]'))).toBe('Sent a test notification. Check that it arrived.');
  });

  it("says why a test notification didn't go", async () => {
    await answer(request('GET', channelsUrl), [channel()]);

    button('Send a test', page.querySelector('.channel')!).click();
    await fail(request('POST', `${channelsUrl}/1/test`), 502, {
      title: 'Bad Gateway',
      status: 502,
      detail: 'Pushover refused the notification (400): application token is invalid',
    });
    // The page shows the outcome once the request's promise settles, after the response
    await stable();

    expect(textOf(page.querySelector('.channel [role=alert]'))).toBe(
      "Couldn't send a test: Pushover refused the notification (400): application token is invalid",
    );
  });

  it('deletes a channel once the Owner confirms, naming the watches that stop using it', async () => {
    await answer(request('GET', channelsUrl), [
      channel({ watches: [{ id: 4, name: 'Price' }, { id: 3, name: 'Shoe stock' }] }),
      channel({ id: 2, name: 'Tablet' }),
    ]);

    button('Delete', page.querySelector('.channel')!).click();
    await stable();
    const dialog = document.querySelector('[role=alertdialog]')!;
    expect(textOf(dialog)).toContain('Delete Phone?');
    expect(textOf(dialog)).toContain('Price and Shoe stock stop sending notifications to it.');
    [...dialog.querySelectorAll<HTMLButtonElement>('button')][1].click();
    await stable();

    await nextRequest(request('DELETE', `${channelsUrl}/1`)).then((r) => r.flush(''));
    await answer(request('GET', channelsUrl), [channel({ id: 2, name: 'Tablet' })]);

    expect([...page.querySelectorAll('.channel h2')].map(textOf)).toEqual(['Tablet']);
  });
});
