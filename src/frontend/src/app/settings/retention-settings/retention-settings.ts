import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, output } from '@angular/core';
import { FormField, form, max, min, required, submit, validate } from '@angular/forms/signals';
import { firstValueFrom } from 'rxjs';
import { problemMessage } from '../../shared/problem';
import { refreshableHttpResource } from '../../shared/refreshable-http-resource';

/** The backend's limits on how many days Runs are kept */
const minRetentionDays = 1;
const maxRetentionDays = 3650;

/** The settings at /api/settings */
interface LookoutSettings {
  retentionDays: number;
}

/**
 * How long Runs are kept before they are pruned. Changes are kept forever. It reports saving through its outputs, for
 * the page to show alongside its other settings' messages.
 */
@Component({
  selector: 'app-retention-settings',
  imports: [FormField],
  templateUrl: './retention-settings.html',
  styleUrl: './retention-settings.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RetentionSettings {
  private readonly http = inject(HttpClient);
  protected readonly settings = refreshableHttpResource<LookoutSettings>(() => ({ url: '/api/settings' }));
  private readonly saved = computed(() => (this.settings.hasValue() ? this.settings.value().retentionDays : undefined));
  // The form is shown only once the settings have loaded, so the placeholder before then is never seen.
  protected readonly model = linkedSignal<number | undefined, LookoutSettings>({
    source: this.saved,
    computation: (retentionDays) => ({ retentionDays: retentionDays ?? 0 }),
  });
  protected readonly retentionForm = form(this.model, (fields) => {
    const message = `Enter a whole number of days between ${minRetentionDays} and ${maxRetentionDays}.`;
    required(fields.retentionDays, { message });
    min(fields.retentionDays, minRetentionDays, { message });
    max(fields.retentionDays, maxRetentionDays, { message });
    validate(fields.retentionDays, (field) => Number.isInteger(field.value()) ? undefined : { kind: 'wholeDays', message });
  });
  readonly saveSucceeded = output<string>();
  readonly saveFailed = output<string>();

  protected async save() {
    await submit(this.retentionForm, async () => {
      try {
        await firstValueFrom(this.http.put<LookoutSettings>('/api/settings', this.model()));
        this.saveSucceeded.emit('Run history settings saved.');
      } catch (error: unknown) {
        this.saveFailed.emit(problemMessage(error, 'Could not save the Run history settings. Please try again.'));
      }
      this.settings.refresh();
    });
  }
}
