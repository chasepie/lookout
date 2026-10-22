import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormField, apply, form, submit } from '@angular/forms/signals';
import { RouterLink } from '@angular/router';
import { ChannelChoices } from '../../channels/channel-choices/channel-choices';
import { Channels } from '../../channels/channels';
import { OutcomeBadge } from '../outcome-badge/outcome-badge';
import { problemMessage } from '../../shared/problem';
import { RunTiming } from '../run-timing/run-timing';
import { defaultSchedule, scheduleSchema } from '../../schedules/schedule';
import { ScheduleEditor } from '../../schedules/schedule-editor/schedule-editor';
import { SchedulePicker } from '../../schedules/schedule-picker/schedule-picker';
import { StatusBadge } from '../status-badge/status-badge';
import { WatchEvents } from '../watch-events';
import { type Watch, outcomeLabels } from '../watch.model';
import { nameAndNotificationUrlRules } from '../watch-settings';
import { Watches } from '../watches';

@Component({
  selector: 'app-watch-list',
  imports: [ChannelChoices, DatePipe, FormField, OutcomeBadge, RouterLink, RunTiming, ScheduleEditor, SchedulePicker, StatusBadge],
  templateUrl: './watch-list.html',
  styleUrl: './watch-list.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WatchList {
  protected readonly watches = inject(Watches);
  private readonly channels = inject(Channels);
  protected readonly model = signal({ name: '', notificationUrl: '', usesVpn: false, schedule: defaultSchedule() });
  protected readonly watchForm = form(this.model, (fields) => {
    nameAndNotificationUrlRules(fields);
    apply(fields.schedule, scheduleSchema);
  });
  protected readonly script = signal<File | undefined>(undefined);
  protected readonly channelIds = signal<ReadonlySet<string>>(new Set());
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly running = signal(new Set<string>());
  /** The Watch whose schedule is being edited, if any. */
  protected readonly editingSchedule = signal<string | undefined>(undefined);
  protected readonly outcomes = outcomeLabels;

  constructor() {
    // Runs, including scheduled ones, and changes made on a Watch's page happen while the list isn't on screen.
    this.watches.list.refresh();
    // While it is, every event changes some Watch on it; the list is read afresh rather than patched, as an event may
    // overtake a read already under way.
    inject(WatchEvents).events.pipe(takeUntilDestroyed()).subscribe(() => this.watches.list.refresh());
  }

  protected selectScript(input: HTMLInputElement) {
    this.script.set(input.files?.[0]);
    this.error.set('');
  }

  protected async createWatch(input: HTMLInputElement) {
    await submit(this.watchForm, async () => {
      const script = this.script();
      if (!script || !/\.(ts|js)$/i.test(script.name) || script.size < 1 || script.size > 65536) {
        this.error.set('Choose a .ts or .js Script between 1 byte and 64 KiB.');
        return;
      }
      this.error.set('');
      this.notice.set('');
      try {
        const { name, notificationUrl, usesVpn, schedule } = this.model();
        await this.watches.create(name, notificationUrl, usesVpn, schedule, script,
          this.channels.stillListed(this.channelIds()));
        this.watchForm().reset({ name: '', notificationUrl: '', usesVpn: false, schedule: defaultSchedule() });
        this.script.set(undefined);
        this.channelIds.set(new Set());
        input.value = '';
        this.notice.set('Watch created. It runs on its schedule, or you can run it now.');
        this.watches.list.refresh();
      } catch (error: unknown) {
        this.error.set(problemMessage(error, 'Could not create the Watch. Check the fields and try again.'));
      }
    });
  }

  protected scheduleSaved({ name }: Watch) {
    this.editingSchedule.set(undefined);
    this.error.set('');
    this.notice.set(`Schedule of ${name} saved.`);
    this.watches.list.refresh();
  }

  protected async runNow({ id, name }: Watch) {
    this.running.update((ids) => new Set(ids).add(id));
    this.error.set('');
    this.notice.set('');
    try {
      const run = await this.watches.runNow(id);
      // Announced through the status region; the reloaded list itself isn't live, so cards aren't re-read.
      this.notice.set(`Run of ${name} finished: ${this.outcomes[run.outcome]}.`);
      this.watches.list.refresh();
    } catch (error: unknown) {
      this.error.set(problemMessage(error, 'Could not run the Watch. Please try again.'));
    } finally {
      this.running.update((ids) => {
        const next = new Set(ids);
        next.delete(id);
        return next;
      });
    }
  }
}
