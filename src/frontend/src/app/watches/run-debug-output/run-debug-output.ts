import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { type DebugOutput, debugOutputKeptPerWatch } from '../watch.model';

/** A Known failure or Errored Run's screenshot and console output, loaded once the user opens them. */
@Component({
  selector: 'app-run-debug-output',
  templateUrl: './run-debug-output.html',
  styleUrl: './run-debug-output.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunDebugOutput {
  readonly watchId = input.required<string>();
  readonly runId = input.required<number>();

  protected readonly open = signal(false);
  // A finished Run's debug output never changes, so closing it keeps what was read for the next time it opens.
  private readonly opened = signal(false);
  private readonly url = computed(() => `/api/watches/${this.watchId()}/runs/${this.runId()}`);
  // A screenshot can be large, so a Run's debug output is read only when it is first opened.
  protected readonly debugOutput = httpResource<DebugOutput>(() => (this.opened() ? `${this.url()}/debug-output` : undefined));
  protected readonly screenshotUrl = computed(() => `${this.url()}/screenshot`);
  // The screenshot can be dropped between reading the debug output and loading the image.
  protected readonly screenshotMissing = signal(false);
  protected readonly missing = computed(() => {
    const error = this.debugOutput.error();
    return error instanceof HttpErrorResponse && error.status === 404;
  });
  protected readonly keptPerWatch = debugOutputKeptPerWatch;

  protected toggle() {
    this.opened.set(true);
    this.open.set(!this.open());
  }
}
