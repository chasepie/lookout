# Frontend (Angular)

The Lookout web UI, an Angular v22 app. These rules apply to everything under `src/frontend/`. The TypeScript rules in the root `CLAUDE.md` apply here as well.

Load the `angular-developer` skill before writing or reviewing Angular code here. The rules below are this project's additions to it. The skill
lives in `.agents/skills/` (pinned by `skills-lock.json`); update it with `npx skills update angular-developer --project`.

## Conventions

- Leave Angular's defaults implicit: no `standalone: true`, and no `changeDetection: ChangeDetectionStrategy.OnPush`.
- Bind classes and styles with `[class.name]` and `[style.prop]`, not `ngClass` or `ngStyle`. Import only the directives and pipes a template
uses, never `CommonModule`.
- Use `NgOptimizedImage` for static images; it can't show inline base64 images, such as run screenshots.
- Keep components and services small, each with one job. Small components keep their template and styles inline; larger ones put them in
`.html` and `.scss` files beside the component, referenced by relative path.

## Accessibility

- Every page MUST pass all AXE checks and meet WCAG AA, including focus management, colour contrast and ARIA attributes.
- Check with AXE in light and dark mode, at desktop and phone widths (down to 320px), including with menus and dialogs open.

## Design

The UI borrows from a ship's deck log and a nautical chart. Tokens (colour, type scale, spacing) are in `src/styles/_tokens.scss`; shared
control classes (`.button`, `.field`, `.input`, `.callout`, …) are in `src/styles/_controls.scss`. Build new UI from these so it matches.

- Type has two roles: the serif italic (`.observation`, or `RunOutcome`) is reserved for what a watched page said, such as run content;
everything Lookout itself says is in Barlow.
- Colour has fixed meanings: `--deep` is for what can be acted on, `--shoal` for what's in progress or new, `--magenta` for failures (in place
of red), and `--buff` only for the VPN's current region.
- A run log reads newest first, grouped by day, with a ditto mark where a run saw the same as the one above (`sameAsAbove`).
- Report an action's outcome through `Notices` (`say` / `fail`), and ask before destroying anything through `Confirm`, never `window.confirm`.
- Reload data on live updates with `reloadOn(resource, ...events)`, never by calling `reload()` directly: a resource ignores `reload()`
while it's loading, and `reloadOn` runs it again once that load ends. A watch's runs come through `injectRunLog`, which pages and merges them.
- Phones are designed for, not just shrunk to. Write those conditions with the mixins in `src/styles/_breakpoints.scss`
(`@use 'breakpoints' as bp`), never as raw media queries: `bp.phone` for phone layouts, `bp.touch` to grow targets to 44px (desktop
stays compact), and `bp.hover` around every hover style, so it doesn't stick after a tap. Code that can't use CSS reads `phoneQuery`.
Anything pinned to the bottom of a phone screen must fit within `scroll-padding-bottom` in `_base.scss`, so it never covers what has focus.

## Testing

- Test a page through `RouterTestingHarness` and its HTTP through `HttpTestingController`, as `watch-detail.spec.ts` does. The only stand-in
is `provideFakeLiveUpdates()`, for the SignalR connection; push messages through its subjects.
- `src/app/testing/fixtures.ts` builds sample watches and runs and answers requests. `answer()` waits for the page to settle, which happens
only once every request is answered, so use `respond()` for all but the last request out.
- Coverage counts every file in `src/app`, and `npm run test:ci` fails below the minimums in `angular.json`'s `coverageThresholds`. Raise
them as coverage grows.
