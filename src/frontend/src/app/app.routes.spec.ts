import { Route } from '@angular/router';
import { routes } from './app.routes';

/** Every route in the tree, children included. */
function allRoutes(list: Route[]): Route[] {
  return list.flatMap((route) => [route, ...allRoutes(route.children ?? [])]);
}

describe('routes', () => {
  it('gives every page a title, which the browser tab and screen readers announce', () => {
    // A run opens inside the watch page, which keeps its title
    const pages = allRoutes(routes).filter((route) => route.loadComponent && route.path !== 'runs/:runId');

    expect(pages.length).toBeGreaterThan(0);
    expect(pages.filter((route) => typeof route.title !== 'string').map((route) => route.path)).toEqual([]);
  });
});
