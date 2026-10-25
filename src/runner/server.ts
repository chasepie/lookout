import { createHash, timingSafeEqual } from 'node:crypto';
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { fileKinds, type ExecuteRequest, type ExecuteScript, type FileKind } from './execute.ts';
import { isObject } from './guards.ts';

/** A request the runner can't execute, reported to the caller with a 4xx status */
class InvalidRequestError extends Error {
  readonly status: number;
  readonly headers: Record<string, string>;

  constructor(message: string, status = 400, headers: Record<string, string> = {}) {
    super(message);
    this.status = status;
    this.headers = headers;
  }
}

/** Far more than any Script's source needs; it bounds what one request can make the runner hold */
const maxBodyBytes = 1024 * 1024;

async function readJson(request: IncomingMessage): Promise<unknown> {
  const tooLarge = new InvalidRequestError(`Request body must be at most ${maxBodyBytes} bytes`, 413);
  if (Number(request.headers['content-length']) > maxBodyBytes) {
    throw tooLarge;
  }
  // Without a declared length, read to the end but stop keeping the body once it's too large
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of request) {
    size += (chunk as Buffer).length;
    if (size <= maxBodyBytes) {
      chunks.push(chunk as Buffer);
    }
  }
  if (size > maxBodyBytes) {
    throw tooLarge;
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    throw new InvalidRequestError('Request body must be JSON');
  }
}

function isFileKind(value: unknown): value is FileKind {
  return (fileKinds as readonly unknown[]).includes(value);
}

function requireString(value: unknown, name: string): string {
  if (typeof value !== 'string') {
    throw new InvalidRequestError(`${name} must be a string`);
  }
  return value;
}

/** Copies an optional proxy field across only when it's present, as `exactOptionalPropertyTypes` requires */
function optionalProxyField(proxy: Record<string, unknown>, key: 'username' | 'password' | 'bypass') {
  return proxy[key] === undefined
    ? {}
    : { [key]: requireString(proxy[key], `proxy.${key}`) };
}

/** The longest delay `setTimeout` honours; it fires longer ones after 1 ms */
const maxTimeoutMs = 2_147_483_647;

function parseExecuteRequest(body: unknown): ExecuteRequest {
  if (!isObject(body)) {
    throw new InvalidRequestError('Request body must be a JSON object');
  }

  const { source, fileKind, context, timeoutMs, proxy, alwaysScreenshot } = body;
  if (!isFileKind(fileKind)) {
    throw new InvalidRequestError(`fileKind must be ${fileKinds.map((kind) => `'${kind}'`).join(' or ')}`);
  }

  if (typeof timeoutMs !== 'number' || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > maxTimeoutMs) {
    throw new InvalidRequestError(`timeoutMs must be an integer from 1 to ${maxTimeoutMs}`);
  }

  // A serializer may write a missing proxy as null
  if (proxy !== undefined && proxy !== null && !isObject(proxy)) {
    throw new InvalidRequestError('proxy must be an object');
  }

  // As with proxy, a serializer may write a missing alwaysScreenshot as null
  if (alwaysScreenshot !== undefined && alwaysScreenshot !== null && typeof alwaysScreenshot !== 'boolean') {
    throw new InvalidRequestError('alwaysScreenshot must be a boolean');
  }

  return {
    source: requireString(source, 'source'),
    fileKind,
    context: {
      notificationUrl: requireString(isObject(context) ? context['notificationUrl'] : undefined, 'context.notificationUrl'),
    },
    timeoutMs,
    alwaysScreenshot: alwaysScreenshot ?? false,
    ...(isObject(proxy) && {
      proxy: {
        server: requireString(proxy['server'], 'proxy.server'),
        ...optionalProxyField(proxy, 'username'),
        ...optionalProxyField(proxy, 'password'),
        ...optionalProxyField(proxy, 'bypass'),
      },
    }),
  };
}

function sendJson(response: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}) {
  response.writeHead(status, { ...headers, 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
}

/**
 * Whether the request says its body is JSON. A web page can send a request without a CORS preflight only with a
 * text or form body, so requiring JSON keeps a page that Browserless's Chromium loads from running a Script here.
 */
function isJsonRequest(request: IncomingMessage) {
  return request.headers['content-type']?.split(';')[0]?.trim().toLowerCase() === 'application/json';
}

function digest(value: string) {
  return createHash('sha256').update(value).digest();
}

/**
 * Refuses a request unless it carries the `Authorization` header whose digest is `expected`, telling one without the
 * header from one with the wrong token, as RFC 6750 does. Comparing digests takes the same time however much of the
 * header matches, and whatever its length.
 */
function requireToken(request: IncomingMessage, expected: Buffer) {
  const { authorization } = request.headers;
  if (authorization === undefined) {
    throw new InvalidRequestError('Requests must carry the runner token', 401, { 'www-authenticate': 'Bearer' });
  }
  if (!timingSafeEqual(digest(authorization), expected)) {
    throw new InvalidRequestError("The request's runner token is wrong", 401,
      { 'www-authenticate': 'Bearer error="invalid_token"' });
  }
}

async function handleExecute(request: IncomingMessage, response: ServerResponse, execute: ExecuteScript, expectedToken?: Buffer) {
  // Anything that can reach the runner could otherwise have it run a Script, including Browserless, which loads
  // whatever pages Scripts visit
  if (expectedToken !== undefined) {
    requireToken(request, expectedToken);
  }
  // Browsers send an Origin with every POST, and the backend doesn't. This refuses a page whose domain was rebound
  // to the runner's address, which counts as same-origin and so needs no preflight.
  if (request.headers.origin !== undefined) {
    throw new InvalidRequestError('Requests from web pages are refused', 403);
  }
  if (!isJsonRequest(request)) {
    throw new InvalidRequestError('Content-Type must be application/json', 415);
  }
  const executeRequest = parseExecuteRequest(await readJson(request));
  // A caller that goes away won't read the outcome, so stop the Script rather than let it run to its timeout
  const callerGone = new AbortController();
  response.once('close', () => {
    if (!response.writableFinished) {
      callerGone.abort();
    }
  });
  sendJson(response, 200, await execute(executeRequest, callerGone.signal));
}

/**
 * The runner's HTTP API: `POST /execute` runs one Script with `execute` and responds with its outcome. Given a
 * `token`, it refuses requests that don't carry it as a bearer token.
 */
export function createRunnerServer(execute: ExecuteScript, token?: string) {
  const expectedToken = token === undefined ? undefined : digest(`Bearer ${token}`);
  return createServer((request, response) => {
    if (request.method !== 'POST' || request.url !== '/execute') {
      sendJson(response, 404, { error: 'Not found' });
      return;
    }

    handleExecute(request, response, execute, expectedToken).catch((error: unknown) => {
      if (error instanceof InvalidRequestError) {
        sendJson(response, error.status, { error: error.message }, error.headers);
        return;
      }
      console.error('Executing a Script failed', error);
      if (!response.headersSent) {
        sendJson(response, 500, { error: 'Internal runner error' });
      }
    });
  });
}
