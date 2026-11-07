import { Component, input, model, signal } from '@angular/core';
import { LucideFileCode } from '@lucide/angular';

/** Picks a watch script, by browsing or dropping a file on it. */
@Component({
  selector: 'app-script-picker',
  imports: [LucideFileCode],
  template: `
    <label
      class="zone"
      [class.zone-over]="over()"
      [class.zone-chosen]="file()"
      (dragover)="dragOver($event)"
      (dragleave)="over.set(false)"
      (drop)="drop($event)"
    >
      <input
        class="visually-hidden"
        type="file"
        accept=".ts,.js"
        [id]="inputId()"
        [required]="required()"
        [attr.aria-describedby]="describedBy()"
        (change)="pick($event)"
      />
      <svg lucideFileCode aria-hidden="true" />
      @if (file(); as chosen) {
        <code class="chosen">{{ chosen.name }}</code>
        <span class="browse">Choose another</span>
      } @else {
        <span>Drop a <code>.ts</code> or <code>.js</code> file here, or <span class="browse">browse</span></span>
      }
    </label>
    @if (rejected(); as message) {
      <span class="field-error" role="alert">{{ message }}</span>
    }
  `,
  styles: `
    @use 'breakpoints' as bp;

    .zone {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      max-width: 32rem;
      padding: var(--space-4);
      border: 1px dashed var(--control-border);
      border-radius: var(--radius-control);
      background: var(--paper-raised);
      cursor: pointer;

      @include bp.hover {
        &:hover {
          border-color: var(--deep);
          background: var(--shoal);
        }
      }

      &.zone-over {
        border-color: var(--deep);
        background: var(--shoal);
      }

      &:focus-within {
        outline: 2px solid var(--deep);
        outline-offset: 2px;
      }

      svg {
        flex: none;
        width: 1.5rem;
        height: 1.5rem;
        color: var(--ink-muted);
      }
    }

    .zone-chosen {
      border-style: solid;
    }

    .chosen {
      flex: 1;
      overflow-wrap: anywhere;
    }

    .browse {
      color: var(--deep);
      text-decoration: underline;
      text-underline-offset: 0.18em;
    }
  `,
})
export class ScriptPicker {
  readonly file = model<File | null>(null);
  readonly inputId = input.required<string>();
  readonly required = input(false);
  readonly describedBy = input<string | null>(null);

  protected readonly over = signal(false);
  /** Why the last file offered wasn't taken. */
  protected readonly rejected = signal<string | null>(null);

  protected pick(event: Event) {
    const input = event.target as HTMLInputElement;
    const picked = input.files?.[0];
    if (picked) {
      this.choose(picked);
    }
  }

  protected dragOver(event: DragEvent) {
    event.preventDefault();
    this.over.set(true);
  }

  protected drop(event: DragEvent) {
    event.preventDefault();
    this.over.set(false);
    const dropped = event.dataTransfer?.files[0];
    if (dropped) {
      this.choose(dropped);
    }
  }

  /** Takes a script, or says why a file isn't one: a drop skips the input's `accept`, and that's only a hint anyway. */
  private choose(file: File) {
    if (/\.(ts|js)$/i.test(file.name)) {
      this.rejected.set(null);
      this.file.set(file);
    } else {
      this.rejected.set(`${file.name} isn't a script. Choose a .ts or .js file.`);
    }
  }
}
