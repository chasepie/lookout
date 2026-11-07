// @ts-check
// The dev server's proxy to the Backend, so the app and the API share one origin, as production's reverse proxy makes
// them. See README.md.

/** The Backend's launch profile port, which the AppHost keeps. */
const target = 'https://localhost:7070';

/**
 * Presents a request from the dev server's own page as coming from the Backend's origin.
 *
 * The Backend's CSRF check wants `Origin` to match the origin it's served on, unless `Sec-Fetch-Site` says
 * same-origin. Browsers send WebSocket handshakes, such as the live updates hub's, without `Sec-Fetch-Site`, and here
 * the page's origin (`http://localhost:4200`) isn't the Backend's, so without this the hub is refused and falls back
 * to a slower transport. Any other origin passes through unchanged, and the Backend still refuses it.
 *
 * @param {import('node:http').ClientRequest} proxyRequest the request on its way to the Backend
 * @param {import('node:http').IncomingMessage} request the request the dev server received
 */
function asBackendOrigin(proxyRequest, request) {
  const { origin, host } = request.headers;
  if (origin === `http://${host}` || origin === `https://${host}`) {
    proxyRequest.setHeader('Origin', target);
  }
}

export default {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
    /** @param {import('node:events').EventEmitter} proxy */
    configure: (proxy) => {
      proxy.on('proxyReq', asBackendOrigin);
      proxy.on('proxyReqWs', asBackendOrigin);
    },
  },
};
