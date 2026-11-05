# @chasepie/lookout-frontend

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 22.2.5.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the
source files.

The dev server forwards `/api` to the Backend (see `proxy.conf.json`), so the app and the API share one origin. Signing in depends on that: the session cookie
is `Secure` and `SameSite=Strict`, and the Backend rejects requests that change data unless they come from the same origin. Chrome and Firefox treat
`http://localhost` as secure. Safari doesn't accept `Secure` cookies over plain HTTP, so develop in Safari with `ng serve --ssl`.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the Backend's `wwwroot` directory (`src/Backend/wwwroot`, ignored by git), where the
Backend serves them. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the
[Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
