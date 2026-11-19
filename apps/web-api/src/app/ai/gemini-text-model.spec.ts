import { GeminiTextModel } from './gemini-text-model';
import { TextModelError } from './text-model';

const reply = (status: number, body: unknown) =>
  ({ ok: status >= 200 && status < 300, status, json: async () => body, text: async () => JSON.stringify(body) }) as unknown as Response;

const answer = (text: string) => reply(200, { candidates: [{ content: { parts: [{ text }] }, finishReason: 'STOP' }] });

const build = (fetchFn: jest.Mock, model = 'gemini-flash-latest') => new GeminiTextModel('AIzaSyEXAMPLEKEY1234', model, fetchFn as never, 5_000);

const fails = async (promise: Promise<unknown>): Promise<TextModelError> => {
  try {
    await promise;
  } catch (error) {
    return error as TextModelError;
  }
  throw new Error('expected a failure');
};

describe('GeminiTextModel', () => {
  it('sends the instructions and the text, with the key in a header and not in the address', async () => {
    const fetchFn = jest.fn().mockResolvedValue(answer('Titulo\nResumen'));

    const text = await build(fetchFn).complete('Resume', 'Ecopetrol anunció…');

    expect(text).toBe('Titulo\nResumen');
    const [url, init] = fetchFn.mock.calls[0];
    expect(url).toBe('https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent');
    expect(url).not.toContain('AIzaSy');
    expect(init.headers['x-goog-api-key']).toBe('AIzaSyEXAMPLEKEY1234');
    expect(JSON.parse(init.body)).toEqual({
      systemInstruction: { parts: [{ text: 'Resume' }] },
      contents: [{ role: 'user', parts: [{ text: 'Ecopetrol anunció…' }] }],
    });
  });

  it('accepts a model written as "models/<id>"', async () => {
    const fetchFn = jest.fn().mockResolvedValue(answer('x'));

    await build(fetchFn, 'models/gemini-flash-latest').complete('i', 't');

    expect(fetchFn.mock.calls[0][0]).toContain('/models/gemini-flash-latest:generateContent');
  });

  it('labels itself with the end of its key only', () => {
    expect(build(jest.fn()).label).toBe('gemini …1234');
  });

  it.each([
    [429, 'rate-limit'],
    [401, 'auth'],
    [403, 'auth'],
    [500, 'unavailable'],
    [503, 'unavailable'],
    [404, 'rejected'],
    [400, 'rejected'],
  ])('maps HTTP %i to "%s"', async (status, kind) => {
    const error = await fails(build(jest.fn().mockResolvedValue(reply(status, { error: { message: 'x' } }))).complete('i', 't'));

    expect(error).toBeInstanceOf(TextModelError);
    expect(error.kind).toBe(kind);
    expect(error.status).toBe(status);
  });

  it('knows an invalid key even though Google answers it with a 400', async () => {
    const body = { error: { status: 'INVALID_ARGUMENT', message: 'API key not valid. Please pass a valid API key.', details: [{ reason: 'API_KEY_INVALID' }] } };

    const error = await fails(build(jest.fn().mockResolvedValue(reply(400, body))).complete('i', 't'));

    expect(error.kind).toBe('auth');
  });

  it('treats a network failure or a timeout as unavailable', async () => {
    const error = await fails(build(jest.fn().mockRejectedValue(new TypeError('fetch failed'))).complete('i', 't'));

    expect(error.kind).toBe('unavailable');
  });

  it('rejects an answer with no text and says why', async () => {
    const blocked = reply(200, { promptFeedback: { blockReason: 'SAFETY' } });

    const error = await fails(build(jest.fn().mockResolvedValue(blocked)).complete('i', 't'));

    expect(error.kind).toBe('rejected');
    expect(error.message).toContain('SAFETY');
  });

  it('never puts the key in an error', async () => {
    const error = await fails(build(jest.fn().mockResolvedValue(reply(500, { error: { message: 'down' } }))).complete('i', 't'));

    expect(error.message).not.toContain('AIzaSyEXAMPLEKEY1234');
  });
});
