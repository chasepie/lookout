import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { type WatchStatus, statusLabels } from '../watch.model';

/** A Watch's status, as a badge coloured by the status, with Running alongside it while a Run is in progress. */
@Component({
  selector: 'app-status-badge',
  templateUrl: './status-badge.html',
  styleUrl: './status-badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"status-" + status()', '[class.running]': 'running()' },
})
export class StatusBadge {
  readonly status = input.required<WatchStatus>();
  readonly running = input(false);
  protected readonly label = computed(() => (this.running() ? `Running (${statusLabels[this.status()]})` : statusLabels[this.status()]));
}
