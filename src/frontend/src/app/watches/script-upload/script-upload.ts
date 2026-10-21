import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, linkedSignal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { OutcomeBadge } from '../outcome-badge/outcome-badge';
import { problemMessage } from '../../shared/problem';
import { refreshableHttpResource } from '../../shared/refreshable-http-resource';
import type { TestRun, Watch } from '../watch.model';
import { Watches } from '../watches';

/**
 * Uploads a new Script version for a Watch. Choosing a file tries it at once with a Test run, which is never recorded
 * and never affects the Watch, so the user can check it before saving it.
 */
@Component({
  selector: 'app-script-upload',
  imports: [DecimalPipe, OutcomeBadge, RouterLink],
  templateUrl: './script-upload.html',
  styleUrl: './script-upload.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ScriptUpload {
  private readonly watches = inject(Watches);
  private readonly router = inject(Router);
  readonly id = input.required<string>();

  protected readonly watch = refreshableHttpResource<Watch>(() => ({ url: `/api/watches/${this.id()}` }));
  // Moving to another Watch's upload page reuses this component, so everything chosen here resets.
  protected readonly script = linkedSignal<string, File | undefined>({ source: this.id, computation: () => undefined });
  protected readonly keepBaseline = linkedSignal({ source: this.id, computation: () => false });
  protected readonly testRun = linkedSignal<string, TestRun | undefined>({ source: this.id, computation: () => undefined });
  protected readonly testing = linkedSignal({ source: this.id, computation: () => false });
  protected readonly saving = linkedSignal({ source: this.id, computation: () => false });
  protected readonly error = linkedSignal({ source: this.id, computation: () => '' });
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
    });
  }

  protected selectScript(input: HTMLInputElement) {
    this.script.set(input.files?.[0]);
    void this.runTest();
  }

  protected async runTest() {
    const id = this.id();
    const script = this.script();
    this.testRun.set(undefined);
    this.error.set('');
    if (!script) {return;}
    this.testing.set(true);
    // A Test run of a file since replaced, or of another Watch, must not show as this one's.
    const isCurrent = () => !this.destroyed && id === this.id() && script === this.script();
    try {
      const result = await this.watches.testRun(id, script);
      if (isCurrent()) {this.testRun.set(result);}
    } catch (error: unknown) {
      if (isCurrent()) {this.error.set(problemMessage(error, 'Could not run the Test run. Please try again.'));}
    } finally {
      if (isCurrent()) {this.testing.set(false);}
    }
  }

  protected async save() {
    const id = this.id();
    const script = this.script();
    if (!script || this.saving()) {return;}
    this.saving.set(true);
    this.error.set('');
    try {
      await this.watches.uploadScript(id, script, this.keepBaseline());
      if (!this.destroyed && id === this.id()) {
        await this.router.navigate(['/watches', id]);
      }
    } catch (error: unknown) {
      if (!this.destroyed && id === this.id()) {
        this.error.set(problemMessage(error, 'Could not save the Script. Please try again.'));
        this.saving.set(false);
      }
    }
  }

  protected screenshotUrl(screenshot: string) {
    return `data:image/png;base64,${screenshot}`;
  }
}
