import { Service, signal } from '@angular/core';

export interface Notice {
  id: number;
  text: string;
  /** A failure stays until it's dismissed; anything else goes after a few seconds. */
  tone: 'info' | 'failure';
}

const infoMs = 6_000;

/**
 * Short messages about what an action did, shown in one place at the edge of every page: "Created run #12",
 * or why deleting a watch failed.
 */
@Service()
export class Notices {
  private readonly current = signal<Notice[]>([]);
  readonly all = this.current.asReadonly();
  private nextId = 1;

  /** Says what just happened. */
  say(text: string) {
    const id = this.add(text, 'info');
    setTimeout(() => this.dismiss(id), infoMs);
  }

  /** Says what went wrong, until dismissed. */
  fail(text: string) {
    this.add(text, 'failure');
  }

  dismiss(id: number) {
    this.current.update((notices) => notices.filter((n) => n.id !== id));
  }

  private add(text: string, tone: Notice['tone']) {
    const id = this.nextId++;
    // A repeat of the same message replaces the old one, so pressing a button twice doesn't stack them
    this.current.update((notices) => [...notices.filter((n) => n.text !== text), { id, text, tone }]);
    return id;
  }
}
