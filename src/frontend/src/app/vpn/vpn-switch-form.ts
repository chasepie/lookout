import { Component, inject, input, signal } from '@angular/core';
import { FormField, form, submit } from '@angular/forms/signals';
import { LucideShuffle } from '@lucide/angular';
import { describeError } from '../shared/describe-error';
import { VpnApi } from './vpn-api';

/** Switches the VPN to a chosen region, or a random one, right away. */
@Component({
  selector: 'app-vpn-switch-form',
  imports: [FormField, LucideShuffle],
  template: `
    <h2 id="switch-title">Switch region</h2>
    <form class="form" aria-labelledby="switch-title" (submit)="switchNow($event)">
      <div class="field">
        <label class="field-label" for="switch-region">Region</label>
        <select id="switch-region" class="select" aria-describedby="switch-hint" [formField]="switchForm.region">
          <option value="">Any region rotation can pick</option>
          @for (region of regions(); track region) {
            <option [value]="region">{{ region }}</option>
          }
        </select>
        <span id="switch-hint" class="field-hint">
          You can pick any region here, excluded ones too. Switching restarts the rotation timer.
        </span>
      </div>
      @if (error(); as error) {
        <div class="callout callout-failure" role="alert"><p>Couldn't switch: {{ error }}</p></div>
      }
      <button type="submit" class="button button-primary" [disabled]="switching() || switchForm().submitting()">
        <svg lucideShuffle aria-hidden="true" />
        {{ switching() ? 'Switching…' : 'Switch now' }}
      </button>
    </form>
  `,
  styles: `
    :host {
      display: grid;
      align-content: start;
      gap: var(--space-4);
    }

    .form {
      display: grid;
      gap: var(--space-4);
    }

    .button {
      justify-self: start;
    }
  `,
})
export class VpnSwitchForm {
  private readonly api = inject(VpnApi);

  readonly regions = input.required<string[]>();
  /** Whether a switch is already in progress; another one would wait for it. */
  readonly switching = input(false);

  protected readonly error = signal<string | null>(null);
  private readonly model = signal({ region: '' });
  protected readonly switchForm = form(this.model);

  protected switchNow(event: Event) {
    event.preventDefault();
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.switchForm, async () => {
      this.error.set(null);
      try {
        await this.api.switchRegion(this.model().region || null);
      } catch (e) {
        this.error.set(describeError(e));
      }
      return undefined;
    });
  }
}
