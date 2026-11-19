/** Injection token of the text model the API uses (see ai.module.ts). */
export const TEXT_MODEL = Symbol('TEXT_MODEL');

/**
 * What a provider says when a call fails, in the terms the failover cares about:
 * - rate-limit: quota or rate exhausted (HTTP 429)
 * - auth: the key is invalid, expired or has no permission
 * - unavailable: the provider is down, unreachable or timed out
 * - rejected: the request itself is the problem (bad model, blocked content): the same text fails with any key
 */
export type TextModelErrorKind = 'rate-limit' | 'auth' | 'unavailable' | 'rejected';

export class TextModelError extends Error {
  constructor(
    readonly kind: TextModelErrorKind,
    message: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = 'TextModelError';
  }
}

/** The one thing the API asks of an AI provider: text in, text out. The prompt and its parsing stay with the caller. */
export interface TextModel {
  /** Names the model in logs and errors. Never includes a whole key. */
  readonly label: string;
  complete(instructions: string, input: string): Promise<string>;
}

/** The error kind for an HTTP status that says nothing more specific. */
export function kindFromStatus(status: number): TextModelErrorKind {
  if (status === 429) return 'rate-limit';
  if (status === 401 || status === 403) return 'auth';
  if (status >= 500) return 'unavailable';
  return 'rejected';
}

/** The end of a key, enough to tell keys apart in a log without giving it away. */
export function keyTail(key: string): string {
  return `…${key.slice(-4)}`;
}
