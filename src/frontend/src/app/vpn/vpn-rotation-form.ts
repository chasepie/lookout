import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormField, form, max, min, required, submit } from '@angular/forms/signals';
import { describeError } from '../shared/describe-error';
import { VpnApi } from './vpn-api';
import { VpnSettings, VpnSettingsDto } from './vpn.model';

/** Rotation needs a region other than the current one, so fewer than this many leaves nothing to rotate to. */
const minimumIncludedRegions = 2;

const sameSettings = (a: VpnSettingsDto, b: VpnSettingsDto) => JSON.stringify(a) === JSON.stringify(b);

/** Edits how often the VPN rotates region and which regions rotation skips. */
@Component({
  selector: 'app-vpn-rotation-form',
  imports: [FormField],
  styles: `
    .region-list {
      list-style: none;
      padding: 0;
      max-height: 20rem;
      overflow-y: auto;
      columns: 14rem auto;
      border: 1px solid #999;
      padding: 0.5rem;
    }
  `,
  template: `
    <h3>Rotation</h3>
    <form (submit)="save($event)">
      <p>
        <label>
          <input type="checkbox" [formField]="rotationForm.rotationEnabled" />
          Rotate regions automatically
        </label>
      </p>
      <p>
        <label for="interval">Every (minutes)</label><br />
        <input id="interval" type="number" [formField]="rotationForm.rotationIntervalMinutes" />
        @if (rotationForm.rotationIntervalMinutes().invalid()) {
          @for (error of rotationForm.rotationIntervalMinutes().errors(); track error) {
            <br /><span role="alert">{{ error.message }}</span>
          }
        }
      </p>
      <p>
        <label for="jitter">Give or take (minutes)</label><br />
        <input
          id="jitter"
          type="number"
          aria-describedby="jitter-hint"
          [formField]="rotationForm.rotationJitterMinutes"
        />
        <br /><span id="jitter-hint">Each rotation lands at a random point this far either side of the interval.</span>
        @if (rotationForm.rotationJitterMinutes().invalid()) {
          @for (error of rotationForm.rotationJitterMinutes().errors(); track error) {
            <br /><span role="alert">{{ error.message }}</span>
          }
        }
      </p>

      <fieldset>
        <legend>Excluded regions ({{ excludedCount() }} of {{ regions().length }})</legend>
        <p id="exclusion-hint">Rotation never picks these. You can still switch to them manually.</p>
        <p>
          <label for="region-filter">Filter regions</label><br />
          <input id="region-filter" type="search" [value]="filter()" (input)="setFilter($event)" />
          <button type="button" [disabled]="visibleRegions().length === 0" (click)="setExcluded(true)">
            Exclude shown
          </button>
          <button type="button" [disabled]="visibleRegions().length === 0" (click)="setExcluded(false)">
            Include shown
          </button>
        </p>
        @if (visibleRegions().length === 0) {
          <p>No regions match.</p>
        } @else {
          <ul class="region-list" aria-describedby="exclusion-hint">
            @for (region of visibleRegions(); track region) {
              <li>
                <label>
                  <input type="checkbox" [checked]="excluded().has(region)" (change)="toggle(region, $event)" />
                  {{ region }}
                </label>
              </li>
            }
          </ul>
        }
        @if (tooFewIncluded()) {
          <p role="alert">Leave at least {{ minimumIncludedRegions }} regions included, or rotation has nowhere to go.</p>
        }
      </fieldset>

      @if (message(); as message) {
        <p role="status">{{ message }}</p>
      }
      @if (error(); as error) {
        <pre role="alert">{{ error }}</pre>
      }
      <button type="submit" [disabled]="rotationForm().submitting() || !dirty()">Save</button>
      <button type="button" [disabled]="!dirty()" (click)="reset()">Discard changes</button>
    </form>
  `,
})
export class VpnRotationForm {
  private readonly api = inject(VpnApi);

  readonly settings = input.required<VpnSettings>();
  readonly regions = input.required<string[]>();

  protected readonly minimumIncludedRegions = minimumIncludedRegions;
  protected readonly filter = signal('');
  protected readonly message = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  /**
   * The saved settings' editable fields. A rotation or switch reloads the settings too, but compares equal
   * here, so it doesn't throw away edits in progress.
   */
  private readonly saved = computed<VpnSettingsDto>(
    () => {
      const settings = this.settings();
      return {
        rotationEnabled: settings.rotationEnabled,
        rotationIntervalMinutes: settings.rotationIntervalMinutes,
        rotationJitterMinutes: settings.rotationJitterMinutes,
        excludedRegions: settings.excludedRegions,
      };
    },
    { equal: sameSettings },
  );

  private readonly model = linkedSignal(() => this.saved());
  protected readonly rotationForm = form(this.model, (path) => {
    required(path.rotationIntervalMinutes, { message: 'Enter how many minutes between rotations.' });
    min(path.rotationIntervalMinutes, 1, { message: 'Rotate at most once a minute.' });
    required(path.rotationJitterMinutes, { message: 'Enter 0 to rotate exactly on the interval.' });
    min(path.rotationJitterMinutes, 0, { message: "Can't be negative." });
    max(path.rotationJitterMinutes, (context) => context.valueOf(path.rotationIntervalMinutes) - 1, {
      message: 'Must be less than the interval.',
    });
  });

  protected readonly dirty = computed(() => !sameSettings(this.model(), this.saved()));

  protected readonly excluded = computed(() => new Set(this.model().excludedRegions));

  /** Counts only regions that still exist, since an exclusion outlives a region the provider drops. */
  protected readonly excludedCount = computed(() => this.regions().filter((r) => this.excluded().has(r)).length);

  protected readonly tooFewIncluded = computed(
    () => this.regions().length > 0 && this.regions().length - this.excludedCount() < minimumIncludedRegions,
  );

  protected readonly visibleRegions = computed(() => {
    const filter = this.filter().trim().toLowerCase();
    return filter ? this.regions().filter((r) => r.toLowerCase().includes(filter)) : this.regions();
  });

  protected setFilter(event: Event) {
    this.filter.set((event.target as HTMLInputElement).value);
  }

  protected toggle(region: string, event: Event) {
    this.updateExcluded([region], (event.target as HTMLInputElement).checked);
  }

  protected setExcluded(exclude: boolean) {
    this.updateExcluded(this.visibleRegions(), exclude);
  }

  protected reset() {
    this.model.set(this.saved());
    this.message.set(null);
    this.error.set(null);
  }

  private updateExcluded(regions: string[], exclude: boolean) {
    this.message.set(null);
    this.model.update((settings) => {
      const excluded = new Set(settings.excludedRegions);
      for (const region of regions) {
        if (exclude) {
          excluded.add(region);
        } else {
          excluded.delete(region);
        }
      }
      return { ...settings, excludedRegions: [...excluded].sort((a, b) => a.localeCompare(b)) };
    });
  }

  protected save(event: Event) {
    event.preventDefault();
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.rotationForm, async () => {
      this.message.set(null);
      this.error.set(null);
      try {
        await this.api.updateSettings(this.model());
        this.message.set('Saved.');
      } catch (e) {
        this.error.set(describeError(e));
      }
      return undefined;
    });
  }
}
