import { ChangeDetectionStrategy, Component, inject, linkedSignal, output } from '@angular/core';
import { FormField, form, pattern, required, submit } from '@angular/forms/signals';
import { problemMessage } from '../../shared/problem';
import { Watches } from '../../watches/watches';
import { InstallTimeZone } from '../install-time-zone';

/** The time zones the browser knows, offered as suggestions; the backend decides which it accepts. */
const suggestions = Intl.supportedValuesOf('timeZone');

/**
 * The install-wide time zone that daily and cron Schedules use. It reports saving through its outputs, for the page to
 * show alongside its other settings' messages.
 */
@Component({
  selector: 'app-time-zone-settings',
  imports: [FormField],
  templateUrl: './time-zone-settings.html',
  styleUrl: './time-zone-settings.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimeZoneSettings {
  protected readonly timeZone = inject(InstallTimeZone);
  private readonly watches = inject(Watches);
  protected readonly suggestions = suggestions;
  // The form is shown only once the settings have loaded, so the placeholder before then is never seen.
  protected readonly model = linkedSignal(() => ({ timeZone: this.timeZone.current() ?? '' }));
  protected readonly timeZoneForm = form(this.model, (fields) => {
    const message = 'Enter a time zone, such as Europe/London.';
    required(fields.timeZone, { message });
    pattern(fields.timeZone, /\S/, { message });
  });
  readonly saveSucceeded = output<string>();
  readonly saveFailed = output<string>();

  protected async save() {
    await submit(this.timeZoneForm, async () => {
      try {
        const saved = await this.timeZone.change(this.model().timeZone);
        // Daily and cron Watches have new next Runs.
        this.watches.list.refresh();
        this.saveSucceeded.emit(`Time zone saved. Daily and cron Watches now run in ${saved.timeZone}.`);
      } catch (error: unknown) {
        this.saveFailed.emit(problemMessage(error, 'Could not save the time zone. Please try again.'));
        // Show what is saved, which may have changed elsewhere.
        this.timeZone.settings.refresh();
      }
    });
  }
}
