import { Component, effect, inject, input, signal } from '@angular/core';
import { FormField, form, required, submit } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { CreateWatchDto, Watch, WatchScheduleKind } from './watch.model';
import { describeError } from '../shared/describe-error';
import { WatchesApi } from './watches-api';

interface WatchFormModel {
  name: string;
  scheduleEnabled: boolean;
  /** The numeric enum value as a string, or `''` for none. */
  scheduleKind: string;
  scheduleInterval: string;
  useVpn: boolean;
}

const scheduleKinds = Object.entries(WatchScheduleKind)
  .filter(([, value]) => typeof value === 'number')
  .map(([label, value]) => ({ label, value: String(value) }));

/** Creates a watch, or edits one when the route has an `id`. */
@Component({
  selector: 'app-watch-form',
  imports: [FormField, RouterLink],
  template: `
    <h2>{{ id() ? 'Edit watch' : 'New watch' }}</h2>

    <form (submit)="save($event)">
      <p>
        <label for="name">Name</label><br />
        <input id="name" [formField]="watchForm.name" />
        @if (watchForm.name().touched() && watchForm.name().invalid()) {
          <br /><span role="alert">Name is required.</span>
        }
      </p>
      <p>
        <label>
          <input type="checkbox" [formField]="watchForm.scheduleEnabled" />
          Schedule enabled
        </label>
      </p>
      <p>
        <label for="kind">Schedule kind</label><br />
        <select id="kind" [formField]="watchForm.scheduleKind">
          <option value="">(none)</option>
          @for (kind of scheduleKinds; track kind.value) {
            <option [value]="kind.value">{{ kind.label }}</option>
          }
        </select>
      </p>
      <p>
        <label for="interval">Schedule interval (a number, or a cron expression)</label><br />
        <input id="interval" [formField]="watchForm.scheduleInterval" />
      </p>
      <p>
        <label>
          <input type="checkbox" aria-describedby="vpn-hint" [formField]="watchForm.useVpn" />
          Use VPN
        </label>
        <br /><span id="vpn-hint">Sends the browser's traffic through the VPN. Runs fail if the VPN isn't set up.</span>
      </p>
      @if (!id()) {
        <p>
          <label for="script">Script (.ts or .js)</label><br />
          <input
            id="script"
            type="file"
            accept=".ts,.js"
            required
            aria-describedby="script-hint"
            (change)="pickScript($event)"
          />
          <br /><span id="script-hint">A watch needs a script to run. You can replace it later.</span>
        </p>
      }

      @if (error(); as error) {
        <pre role="alert">{{ error }}</pre>
      }

      <button type="submit" [disabled]="watchForm().submitting() || (!id() && !script())">Save</button>
      <a [routerLink]="id() ? ['/watches', id()] : ['/']">Cancel</a>
    </form>
  `,
})
export class WatchForm {
  private readonly api = inject(WatchesApi);
  private readonly router = inject(Router);

  /** Route param; absent when creating. */
  readonly id = input<string>();

  protected readonly scheduleKinds = scheduleKinds;
  protected readonly error = signal<string | null>(null);
  /** The script to create the watch with; unused when editing. */
  protected readonly script = signal<File | null>(null);

  private readonly model = signal<WatchFormModel>({
    name: '',
    scheduleEnabled: false,
    scheduleKind: '',
    scheduleInterval: '',
    useVpn: false,
  });
  protected readonly watchForm = form(this.model, (path) => {
    required(path.name);
  });

  private readonly existing = this.api.watchResource(this.id);

  constructor() {
    effect(() => {
      if (!this.id() || !this.existing.hasValue()) {
        return;
      }
      const watch = this.existing.value();
      this.model.set({
        name: watch.name,
        scheduleEnabled: watch.scheduleEnabled,
        scheduleKind: watch.scheduleKind === null ? '' : String(watch.scheduleKind),
        scheduleInterval: watch.scheduleInterval ?? '',
        useVpn: watch.useVpn,
      });
    });
  }

  protected pickScript(event: Event) {
    const input = event.target as HTMLInputElement;
    this.script.set(input.files?.[0] ?? null);
  }

  protected save(event: Event) {
    event.preventDefault();
    // The action catches its own errors and shows them, so nothing is left to handle on the returned promise
    void submit(this.watchForm, async () => {
      this.error.set(null);
      const value = this.model();
      const dto: CreateWatchDto = {
        name: value.name,
        scheduleEnabled: value.scheduleEnabled,
        scheduleKind: value.scheduleKind === '' ? null : Number(value.scheduleKind),
        scheduleInterval: value.scheduleInterval || null,
        useVpn: value.useVpn,
      };
      try {
        const id = this.id();
        const script = this.script();
        let saved: Watch;
        if (id) {
          saved = await this.api.update(id, dto);
        } else if (script) {
          saved = await this.api.create(dto, script);
        } else {
          this.error.set('Choose a script for the watch.');
          return undefined;
        }
        await this.router.navigate(['/watches', saved.id]);
      } catch (e) {
        this.error.set(describeError(e));
      }
      return undefined;
    });
  }
}
