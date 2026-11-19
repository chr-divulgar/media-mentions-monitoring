import { FailoverTextModel } from './failover-text-model';
import { TextModel, TextModelError, TextModelErrorKind } from './text-model';

const ok = (label: string, text = label): TextModel => ({ label, complete: jest.fn(async () => text) });
const failing = (label: string, kind: TextModelErrorKind, status?: number): TextModel => ({
  label,
  complete: jest.fn(async () => {
    throw new TextModelError(kind, `boom ${kind}`, status);
  }),
});

describe('FailoverTextModel', () => {
  let clock: number;
  const build = (models: TextModel[]) =>
    new FailoverTextModel(models, { now: () => clock, log: jest.fn(), rateLimitCooldownMs: 300_000, unavailableCooldownMs: 30_000 });

  beforeEach(() => {
    clock = 1_000_000;
  });

  it('uses the first model when it works', async () => {
    const [a, b] = [ok('a'), ok('b')];
    await expect(build([a, b]).complete('i', 'x')).resolves.toBe('a');
    expect(b.complete).not.toHaveBeenCalled();
  });

  it('moves on to the next model when one is rate limited, and rests the first one', async () => {
    const [a, b] = [failing('a', 'rate-limit', 429), ok('b')];
    const sut = build([a, b]);

    await expect(sut.complete('i', 'x')).resolves.toBe('b');
    await expect(sut.complete('i', 'x')).resolves.toBe('b');

    // The second call skips the resting model instead of asking it again.
    expect(a.complete).toHaveBeenCalledTimes(1);
  });

  it('asks a resting model again once its rest is over', async () => {
    const [a, b] = [failing('a', 'rate-limit', 429), ok('b')];
    const sut = build([a, b]);
    await sut.complete('i', 'x');

    clock += 300_001;
    await sut.complete('i', 'x');

    expect(a.complete).toHaveBeenCalledTimes(2);
  });

  it('turns a model with an invalid key off for good and warns once', async () => {
    const log = jest.fn();
    const [a, b] = [failing('a', 'auth', 403), ok('b')];
    const sut = new FailoverTextModel([a, b], { now: () => clock, log });

    await sut.complete('i', 'x');
    clock += 10 * 60 * 60 * 1000;
    await sut.complete('i', 'x');

    expect(a.complete).toHaveBeenCalledTimes(1);
    expect(log).toHaveBeenCalledTimes(1);
  });

  it('moves on when a model is unavailable', async () => {
    const [a, b] = [failing('a', 'unavailable', 503), ok('b')];
    await expect(build([a, b]).complete('i', 'x')).resolves.toBe('b');
  });

  it('fails at once on a rejected request: the same text would fail with any key', async () => {
    const [a, b] = [failing('a', 'rejected', 400), ok('b')];

    await expect(build([a, b]).complete('i', 'x')).rejects.toMatchObject({ kind: 'rejected' });
    expect(b.complete).not.toHaveBeenCalled();
  });

  it('still tries models that are resting when none is left, so a single key is never blocked', async () => {
    const a = failing('a', 'unavailable', 503);
    const sut = build([a]);
    await expect(sut.complete('i', 'x')).rejects.toThrow();

    await expect(sut.complete('i', 'x')).rejects.toThrow();

    expect(a.complete).toHaveBeenCalledTimes(2);
  });

  it('says what happened with every model when all fail', async () => {
    const sut = build([failing('gemini …1111', 'rate-limit', 429), failing('openai', 'unavailable', 503)]);

    await expect(sut.complete('i', 'x')).rejects.toThrow(/gemini …1111.*rate-limit.*429.*openai.*unavailable.*503/s);
  });

  it('treats an unexpected error as an unavailable model', async () => {
    const a: TextModel = { label: 'a', complete: jest.fn(async () => { throw new Error('socket hang up'); }) };

    await expect(build([a, ok('b')]).complete('i', 'x')).resolves.toBe('b');
  });

  it('fails clearly when every model is turned off', async () => {
    const sut = build([failing('a', 'auth', 401)]);
    await expect(sut.complete('i', 'x')).rejects.toThrow();

    await expect(sut.complete('i', 'x')).rejects.toThrow(/invalid|inválid/i);
  });
});
