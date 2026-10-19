import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { json } from 'node:stream/consumers';
import { execute, fileKinds, type ExecuteRequest, type FileKind, type RunnerConfig } from './execute.ts';
import { isObject } from './guards.ts';

/** A request the runner can't execute, reported to the caller as a 400 */
class InvalidRequestError extends Error {}

const readJson = (request: IncomingMessage) =>
  json(request).catch(() => {
    throw new InvalidRequestError('Request body must be JSON');
  });

const isFileKind = (value: unknown): value is FileKind => (fileKinds as readonly unknown[]).includes(value);

function requireString(value: unknown, name: string): string {
  if (typeof value !== 'string') {
    throw new InvalidRequestError(`${name} must be a string`);
  }
  return value;
}

/** Copies an optional proxy field across only when it's present, as `exactOptionalPropertyTypes` requires */
const optionalProxyField = (proxy: Record<string, unknown>, key: 'username' | 'password' | 'bypass') =>
  proxy[key] === undefined ? {} : { [key]: requireString(proxy[key], `proxy.${key}`) };

/** The longest delay `setTimeout` honours; it fires longer ones after 1 ms */
const maxTimeoutMs = 2_147_483_647;

function parseExecuteRequest(body: unknown): ExecuteRequest {
  if (!isObject(body)) {
    throw new InvalidRequestError('Request body must be a JSON object');
  }
  const { source, fileKind, context, timeoutMs, proxy } = body;
  if (!isFileKind(fileKind)) {
    throw new InvalidRequestError(`fileKind must be ${fileKinds.map((kind) => `'${kind}'`).join(' or ')}`);
  }
  if (typeof timeoutMs !== 'number' || !Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > maxTimeoutMs) {
    throw new InvalidRequestError(`timeoutMs must be an integer from 1 to ${maxTimeoutMs}`);
  }
  if (proxy !== undefined && !isObject(proxy)) {
    throw new InvalidRequestError('proxy must be an object');
  }
  return {
    source: requireString(source, 'source'),
    fileKind,
    context: {
      notificationUrl: requireString(isObject(context) ? context['notificationUrl'] : undefined, 'context.notificationUrl'),
    },
    timeoutMs,
    ...(proxy && {
      proxy: {
        server: requireString(proxy['server'], 'proxy.server'),
        ...optionalProxyField(proxy, 'username'),
        ...optionalProxyField(proxy, 'password'),
        ...optionalProxyField(proxy, 'bypass'),
      },
    }),
  };
}

const sendJson = (response: ServerResponse, status: number, body: unknown) => {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
};

async function handleExecute(request: IncomingMessage, response: ServerResponse, config: RunnerConfig) {
  const executeRequest = parseExecuteRequest(await readJson(request));
  sendJson(response, 200, await execute(executeRequest, config));
}

/** The runner's HTTP API: `POST /execute` runs one Script and responds with its outcome */
export const createRunnerServer = (config: RunnerConfig) =>
  createServer((request, response) => {
    if (request.method !== 'POST' || request.url !== '/execute') {
      sendJson(response, 404, { error: 'Not found' });
      return;
    }
    handleExecute(request, response, config).catch((error: unknown) => {
      if (error instanceof InvalidRequestError) {
        sendJson(response, 400, { error: error.message });
        return;
      }
      console.error('Executing a Script failed', error);
      if (!response.headersSent) {
        sendJson(response, 500, { error: 'Internal runner error' });
      }
    });
  });
