import { Component, inject, input, signal } from '@angular/core';
import { FormField, form, submit } from '@angular/forms/signals';
import { describeError } from '../watches/watches-api';
import { VpnApi } from './vpn-api';

/** Switches the VPN to a chosen region, or a random one, right away. */
@Component({
  selector: 'app-vpn-switch-form',
  imports: [FormField],
  template: `
    <h3>Switch now</h3>
    <form (submit)="switchNow($event)">
      <p>
        <label for="switch-region">Region</label><br />
        <select id="switch-region" aria-describedby="switch-hint" [formField]="switchForm.region">
          <option value="">Random (not excluded from rotation)</option>
          @for (region of regions(); track region) {
            <option [value]="region">{{ region }}</option>
          }
        </select>
        <br /><span id="switch-hint">Any region can be picked here, even excluded ones. Restarts the rotation timer.</span>
      </p>
      @if (error(); as error) {
        <pre role="alert">{{ error }}</pre>
      }
      <button type="submit" [disabled]="switching() || switchForm().submitting()">Switch</button>
    </form>
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
