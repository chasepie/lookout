import { Route } from '@angular/router';
import { routes } from './app.routes';

/** Every route in the tree, children included. */
function allRoutes(list: Route[]): Route[] {
  return list.flatMap((route) => [route, ...allRoutes(route.children ?? [])]);
}

describe('routes', () => {
  it('gives every page a title, which the browser tab and screen readers announce', () => {
    // A run opens inside the watch page, which titles itself with the watch's name
    const selfTitled = ['watches/:id', 'runs/:runId'];
    const pages = allRoutes(routes).filter((route) => route.loadComponent && !selfTitled.includes(route.path ?? ''));

    expect(pages.length).toBeGreaterThan(0);
    expect(pages.filter((route) => typeof route.title !== 'string').map((route) => route.path)).toEqual([]);
  });

  it("leaves the watch page's title to the page, so opening a run doesn't reset it", () => {
    const watchPage = allRoutes(routes).find((route) => route.path === 'watches/:id');

    expect(watchPage?.title).toBeUndefined();
  });
});
