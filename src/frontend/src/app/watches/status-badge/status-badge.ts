import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { type WatchStatus, statusLabels } from '../watch.model';

/** A Watch's status, as a badge coloured by the status. */
@Component({
  selector: 'app-status-badge',
  templateUrl: './status-badge.html',
  styleUrl: './status-badge.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"status-" + status()' },
})
export class StatusBadge {
  readonly status = input.required<WatchStatus>();
  protected readonly statuses = statusLabels;
}
