/**
 * The header the Backend requires on every request that changes data, as protection against cross-site request
 * forgery. Any value works; the point is that a page on another origin can't add it.
 */
export const csrfHeader = 'X-Lookout-Request';

/** {@link csrfHeader} with its value, ready to send. */
export const csrfHeaders: Record<string, string> = { [csrfHeader]: '1' };
