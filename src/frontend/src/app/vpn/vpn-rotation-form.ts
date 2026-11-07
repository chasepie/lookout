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
  styleUrl: './vpn-rotation-form.scss',
  template: `
    <h2 id="rotation-title">Rotation</h2>
    <form class="form" aria-labelledby="rotation-title" (submit)="save($event)">
      <label class="check">
        <input type="checkbox" [formField]="rotationForm.rotationEnabled" />
        <span>Switch to a random region on a timer</span>
      </label>

      <div class="timing">
        <div class="field">
          <label class="field-label" for="interval">Every</label>
          <span class="with-unit">
            <input
              id="interval"
              class="input input-narrow"
              type="number"
              aria-describedby="interval-error"
              [attr.aria-invalid]="rotationForm.rotationIntervalMinutes().invalid()"
              [formField]="rotationForm.rotationIntervalMinutes"
            />
            minutes
          </span>
          <span id="interval-error" class="field-error">
            @for (error of rotationForm.rotationIntervalMinutes().errors(); track error) {
              {{ error.message }}
            }
          </span>
        </div>
        <div class="field">
          <label class="field-label" for="jitter">Give or take</label>
          <span class="with-unit">
            <input
              id="jitter"
              class="input input-narrow"
              type="number"
              aria-describedby="jitter-hint jitter-error"
              [attr.aria-invalid]="rotationForm.rotationJitterMinutes().invalid()"
              [formField]="rotationForm.rotationJitterMinutes"
            />
            minutes
          </span>
          <span id="jitter-hint" class="field-hint">Each switch lands at a random point this far either side.</span>
          <span id="jitter-error" class="field-error">
            @for (error of rotationForm.rotationJitterMinutes().errors(); track error) {
              {{ error.message }}
            }
          </span>
        </div>
      </div>

      <fieldset class="exclusions" aria-describedby="exclusion-hint">
        <legend>Excluded regions <span class="count">{{ excludedCount() }} of {{ regions().length }}</span></legend>
        <p id="exclusion-hint" class="field-hint">Rotation never picks these. You can still switch to them yourself.</p>
        <div class="filter-row">
          <label class="visually-hidden" for="region-filter">Filter regions</label>
          <input
            id="region-filter"
            class="input filter"
            type="search"
            placeholder="Filter regions"
            [value]="filter()"
            (input)="setFilter($event)"
          />
          <button type="button" class="button" [disabled]="visibleRegions().length === 0" (click)="setExcluded(true)">
            Exclude {{ filter() ? 'these' : 'all' }}
          </button>
          <button type="button" class="button" [disabled]="visibleRegions().length === 0" (click)="setExcluded(false)">
            Include {{ filter() ? 'these' : 'all' }}
          </button>
        </div>
        @if (visibleRegions().length === 0) {
          <p class="muted">No regions match "{{ filter() }}".</p>
        } @else {
          <ul class="region-list">
            @for (region of visibleRegions(); track region) {
              <li>
                <label class="check" [class.excluded]="excluded().has(region)">
                  <input type="checkbox" [checked]="excluded().has(region)" (change)="toggle(region, $event)" />
                  <span>{{ region }}</span>
                </label>
              </li>
            }
          </ul>
        }
        @if (tooFewIncluded()) {
          <div class="callout callout-failure" role="alert">
            <p>Leave at least {{ minimumIncludedRegions }} regions included, or rotation has nowhere to go.</p>
          </div>
        }
      </fieldset>

      <div role="status">
        @if (message(); as message) {
          <p class="muted">{{ message }}</p>
        }
      </div>
      @if (error(); as error) {
        <div class="callout callout-failure" role="alert"><p>Couldn't save: {{ error }}</p></div>
      }
      <div class="actions save" [class.save-pending]="dirty()">
        <button type="submit" class="button button-primary" [disabled]="rotationForm().submitting() || !dirty()">
          Save rotation
        </button>
        <button type="button" class="button button-quiet" [disabled]="!dirty()" (click)="reset()">Discard changes</button>
        @if (dirty()) {
          <span class="muted unsaved">Unsaved changes</span>
        }
      </div>
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
        this.message.set('Saved the rotation.');
      } catch (e) {
        this.error.set(describeError(e));
      }
      return undefined;
    });
  }
}
