/** Type guards for checking values that arrive untyped: request bodies, IPC messages and Script outcomes */

export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

export const isOptionalString = (value: unknown) => value === undefined || typeof value === 'string';
