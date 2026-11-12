import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { liveUpdates, provideFakeLiveUpdates } from '../testing/fake-live-updates';
import { answer, fail, nextRequest, request, respond, textOf } from '../testing/fixtures';
import { VpnPage } from './vpn-page';
import { VpnRegions, VpnSettings, VpnStatus } from './vpn.model';

const regions = ['AU Sydney', 'CA Toronto', 'DE Berlin', 'Japan', 'US East'];

function settings(overrides: Partial<VpnSettings> = {}): VpnSettings {
  return {
    id: 1,
    rotationEnabled: true,
    rotationIntervalMinutes: 60,
    rotationJitterMinutes: 10,
    excludedRegions: ['Japan'],
    currentRegion: 'CA Toronto',
    lastSwitchTime: new Date(Date.now() - 38 * 60_000).toISOString(),
    nextRotationTime: new Date(Date.now() + 24 * 60_000).toISOString(),
    lastError: null,
    ...overrides,
  };
}

function status(overrides: Partial<VpnStatus> = {}): VpnStatus {
  return {
    configured: true,
    settings: settings(),
    switch: null,
    publicIp: { publicIp: '198.51.100.24', country: 'Canada', region: 'Ontario', city: 'Toronto', organization: null },
    gluetunError: null,
    ...overrides,
  };
}

describe('VpnPage', () => {
  let page: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'vpn', component: VpnPage }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideFakeLiveUpdates(),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/vpn', VpnPage);
    page = harness.routeNativeElement!;
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  const stable = () => TestBed.inject(ApplicationRef).whenStable();
  const button = (name: string) =>
    [...page.querySelectorAll<HTMLButtonElement>('button')].find((b) => textOf(b) === name)!;

  async function open(vpn = status(), available: VpnRegions = { provider: 'private internet access', regions, limited: false }) {
    await respond(request('GET', '/api/Vpn'), vpn);
    await answer(request('GET', '/api/Vpn/regions'), available);
  }

  it('says how to set the VPN up when it isn’t', async () => {
    await answer(request('GET', '/api/Vpn'), status({ configured: false, settings: null, publicIp: null }));

    expect(textOf(page)).toContain("The VPN isn't set up");
    TestBed.inject(HttpTestingController).expectNone(request('GET', '/api/Vpn/regions'));
  });

  it('shows where the VPN comes out and when it next rotates', async () => {
    await open();

    expect(textOf(page.querySelector('.region-name'))).toBe('CA Toronto');
    const facts = textOf(page.querySelector('.facts'));
    expect(facts).toContain('198.51.100.24');
    expect(facts).toContain('Toronto, Ontario, Canada');
    expect(facts).toContain('38 min ago');
    expect(facts).toContain('in 24 min');
  });

  it('says what a switch in progress is waiting for, and why the last one failed', async () => {
    await open(
      status({
        settings: settings({ lastError: 'Timed out waiting for the tunnel' }),
        switch: { region: 'DE Berlin', waitingForRun: true, startedTime: new Date().toISOString() },
        publicIp: null,
      }),
    );

    expect(textOf(page.querySelector('[role=status]'))).toBe(
      'Switching to DE Berlin once the VPN watch runs in progress finish…',
    );
    expect(textOf(page.querySelector('.facts'))).toContain('Reconnecting…');
    expect(textOf(page.querySelector('[role=alert]'))).toBe('The last switch failed: Timed out waiting for the tunnel');
    expect(button('Switching…').disabled).toBe(true);
  });

  it("says when the provider has no regions to switch to", async () => {
    await open(status(), { provider: 'private internet access', regions: [], limited: false });

    expect(textOf(page.querySelector('[role=alert]'))).toContain('no regions for private internet access');
  });

  it('says when GLUETUN_SERVER_REGIONS matches none of the provider’s regions', async () => {
    await open(status(), { provider: 'private internet access', regions: [], limited: true });

    const alert = textOf(page.querySelector('[role=alert]'));
    expect(alert).toContain('GLUETUN_SERVER_REGIONS');
    expect(alert).not.toContain('no regions for private internet access');
  });

  it('keeps the status shown when a refresh fails', async () => {
    await open();

    liveUpdates().vpnChanged.next();
    await fail(request('GET', '/api/Vpn'), 502, 'gluetun is unreachable');

    expect(textOf(page.querySelector('.region-name'))).toBe('CA Toronto');
    expect(page.querySelector('app-vpn-rotation-form')).not.toBeNull();
    expect(textOf(page.querySelector('.callout-retry p'))).toBe("Couldn't refresh the VPN's status: gluetun is unreachable");
  });

  it('reloads when the VPN changes', async () => {
    await open();

    liveUpdates().vpnChanged.next();
    await answer(request('GET', '/api/Vpn'), status({ settings: settings({ currentRegion: 'DE Berlin' }) }));

    expect(textOf(page.querySelector('.region-name'))).toBe('DE Berlin');
  });

  describe('switching region', () => {
    it('switches to the region chosen', async () => {
      await open();
      const select = page.querySelector<HTMLSelectElement>('#switch-region')!;
      select.value = 'DE Berlin';
      select.dispatchEvent(new Event('input'));
      select.dispatchEvent(new Event('change'));

      button('Switch now').click();
      const switchRequest = await nextRequest(request('POST', '/api/Vpn/switch'));

      expect(switchRequest.request.body).toEqual({ region: 'DE Berlin' });
      switchRequest.flush('');
      await stable();
    });

    it('says why a switch was refused', async () => {
      await open();

      button('Switch now').click();
      const switchRequest = await nextRequest(request('POST', '/api/Vpn/switch'));
      expect(switchRequest.request.body).toEqual({ region: null });
      switchRequest.flush('gluetun is unreachable', { status: 502, statusText: 'Bad Gateway' });

      // The form's submit action settles after the response
      await vi.waitFor(async () => {
        await stable();
        expect(textOf(page.querySelector('app-vpn-switch-form [role=alert]'))).toBe(
          "Couldn't switch: gluetun is unreachable",
        );
      });
    });
  });

  describe('rotation', () => {
    const regionBox = (name: string) =>
      [...page.querySelectorAll<HTMLLabelElement>('.region-list label')]
        .find((label) => textOf(label) === name)!
        .querySelector('input')!;
    const count = () => textOf(page.querySelector('.count'));

    async function filterRegions(text: string) {
      const filter = page.querySelector<HTMLInputElement>('#region-filter')!;
      filter.value = text;
      filter.dispatchEvent(new Event('input'));
      await stable();
    }

    it('excludes regions and saves the change', async () => {
      await open();
      expect(count()).toBe('1 of 5');
      expect(button('Save rotation').disabled).toBe(true);

      regionBox('AU Sydney').click();
      await stable();
      expect(count()).toBe('2 of 5');

      button('Save rotation').click();
      const save = await nextRequest(request('PUT', '/api/Vpn/settings'));
      expect(save.request.body).toEqual({
        rotationEnabled: true,
        rotationIntervalMinutes: 60,
        rotationJitterMinutes: 10,
        excludedRegions: ['AU Sydney', 'Japan'],
      });
      save.flush(settings({ excludedRegions: ['AU Sydney', 'Japan'] }));

      await vi.waitFor(async () => {
        await stable();
        expect(textOf(page.querySelector('app-vpn-rotation-form [role=status]'))).toBe('Saved the rotation.');
      });
    });

    it('excludes or includes every region a filter shows at once', async () => {
      await open();

      await filterRegions('a');
      expect(page.querySelectorAll('.region-list li')).toHaveLength(4);
      button('Exclude these').click();
      await stable();
      expect(count()).toBe('4 of 5');

      await filterRegions('zzz');
      expect(textOf(page)).toContain('No regions match "zzz".');
    });

    it('warns when too few regions are left to rotate between', async () => {
      await open();

      button('Exclude all').click();
      await stable();

      expect(textOf(page.querySelector('app-vpn-rotation-form [role=alert]'))).toContain(
        'Leave at least 2 regions included',
      );
    });

    it('says rotation needs another region when GLUETUN_SERVER_REGIONS allows only one', async () => {
      // Excluding fewer regions can't fix this, so asking the Owner to would be no help
      await open(status({ settings: settings({ excludedRegions: [], currentRegion: 'DE Berlin' }) }), {
        provider: 'private internet access',
        regions: ['DE Berlin'],
        limited: true,
      });

      const alert = textOf(page.querySelector('app-vpn-rotation-form [role=alert]'));
      expect(alert).toContain('GLUETUN_SERVER_REGIONS');
      expect(alert).not.toContain('Leave at least');
    });

    it('throws edits away on request', async () => {
      await open();
      regionBox('AU Sydney').click();
      await stable();

      button('Discard changes').click();
      await stable();

      expect(count()).toBe('1 of 5');
      expect(regionBox('AU Sydney').checked).toBe(false);
    });

    it("explains a jitter that's too large for the interval", async () => {
      await open();
      const jitter = page.querySelector<HTMLInputElement>('#jitter')!;
      jitter.value = '60';
      jitter.dispatchEvent(new Event('input'));
      await stable();

      expect(textOf(page.querySelector('#jitter-error'))).toBe('Must be less than the interval.');
      expect(jitter.getAttribute('aria-invalid')).toBe('true');
    });

    it('says why saving failed', async () => {
      await open();
      regionBox('AU Sydney').click();
      await stable();

      button('Save rotation').click();
      await fail(request('PUT', '/api/Vpn/settings'), 400, 'Too many excluded regions.');

      await vi.waitFor(async () => {
        await stable();
        expect(textOf(page.querySelector('app-vpn-rotation-form [role=alert]'))).toBe(
          "Couldn't save: Too many excluded regions.",
        );
      });
    });
  });
});
