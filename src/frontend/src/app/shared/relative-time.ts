import { Component, computed, inject, input } from '@angular/core';
import { Clock } from './clock';
import { fullTime, relativeTime } from './time';

/** A time shown relative to now, which stays current, with the exact time on hover and for machines. */
@Component({
  selector: 'app-relative-time',
  template: `<time [attr.datetime]="value()" [title]="exact()">{{ text() }}</time>`,
})
export class RelativeTime {
  /** An ISO 8601 time, as the API sends it. */
  readonly value = input.required<string>();

  private readonly clock = inject(Clock);
  private readonly time = computed(() => Date.parse(this.value()));
  protected readonly text = computed(() => relativeTime(this.time(), this.clock.now()));
  protected readonly exact = computed(() => fullTime(this.time()));
}
