import { assembleTranscript, transcribeViaWorker } from './worker-transcription';

const reply = (status: number, body: unknown) =>
  ({ ok: status >= 200 && status < 300, status, json: async () => body }) as unknown as Response;

describe('transcribeViaWorker', () => {
  it('asks the worker for the text of the chunks, in Colombian Spanish, and returns what it answers', async () => {
    const chunks = [
      { status: 'recognized', text: 'hola' },
      { status: 'no_speech', text: '' },
    ];
    const fetchFn = jest.fn().mockResolvedValue(reply(200, { chunks }));

    const result = await transcribeViaWorker(['a.wav', 'b.wav'], { endpoint: 'http://worker:5000', fetchFn: fetchFn as never });

    expect(result).toEqual(chunks);
    const [url, init] = fetchFn.mock.calls[0];
    expect(url).toBe('http://worker:5000/transcribe');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({ audioPaths: ['a.wav', 'b.wav'], language: 'es-CO' });
  });

  it('says the worker could not be reached, instead of a bare "fetch failed"', async () => {
    const fetchFn = jest.fn().mockRejectedValue(new TypeError('fetch failed'));

    await expect(transcribeViaWorker(['a.wav'], { fetchFn: fetchFn as never })).rejects.toThrow(/worker.*transcrip/i);
  });

  it("passes on the worker's own message when it refuses", async () => {
    const fetchFn = jest.fn().mockResolvedValue(reply(503, { message: 'El motor de transcripción no está disponible.' }));

    await expect(transcribeViaWorker(['a.wav'], { fetchFn: fetchFn as never })).rejects.toThrow('El motor de transcripción no está disponible.');
  });

  it('refuses an answer that does not have one result per file', async () => {
    const fetchFn = jest.fn().mockResolvedValue(reply(200, { chunks: [{ status: 'recognized', text: 'x' }] }));

    await expect(transcribeViaWorker(['a.wav', 'b.wav'], { fetchFn: fetchFn as never })).rejects.toThrow(/resultados/);
  });
});

describe('assembleTranscript', () => {
  it('joins the text of every chunk', () => {
    const text = assembleTranscript([
      { status: 'recognized', text: 'primera parte' },
      { status: 'recognized', text: 'segunda parte' },
    ]);

    expect(text).toBe('primera parte segunda parte');
  });

  it('keeps the placeholder the note editor always showed for music or non-Spanish audio', () => {
    expect(assembleTranscript([{ status: 'recognized', text: 'hola' }, { status: 'no_speech', text: '' }])).toBe('hola Música-NoEspañol.');
  });

  it('marks a failed chunk but keeps the rest when some worked', () => {
    const errors = jest.spyOn(console, 'error').mockImplementation(() => undefined);

    expect(assembleTranscript([{ status: 'recognized', text: 'hola' }, { status: 'rate_limited', text: '' }])).toBe('hola --error--');
    expect(errors).toHaveBeenCalled();
    errors.mockRestore();
  });

  it.each([
    ['rate_limited', /límite/i],
    ['api_error', /error/i],
    ['timeout', /tiempo/i],
    ['network_error', /conexión/i],
    ['audio_error', /audio/i],
  ])('says why when every chunk failed (%s)', (status, message) => {
    expect(() => assembleTranscript([{ status, text: '' }, { status, text: '' }])).toThrow(message);
  });
});
