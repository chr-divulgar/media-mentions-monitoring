/** The text of one audio chunk, as the worker's speech engine answers it. */
export interface TranscribedChunk {
  /** recognized | no_speech | audio_error | rate_limited | api_error | timeout | network_error */
  status: string;
  text: string;
}

interface WorkerOptions {
  endpoint?: string;
  fetchFn?: typeof fetch;
  timeoutMs?: number;
}

// How a chunk that failed is told to the person (silence is not a failure: see assembleTranscript).
const FAILURE_MESSAGES: Record<string, string> = {
  rate_limited: 'El servicio de reconocimiento limitó las solicitudes (límite de API). Intenta de nuevo en un momento.',
  api_error: 'El servicio de reconocimiento respondió con un error.',
  timeout: 'El servicio de reconocimiento no respondió a tiempo.',
  network_error: 'Sin conexión con el servicio de reconocimiento.',
  audio_error: 'El audio no se pudo leer.',
};

/**
 * Asks the worker (its speech engine) for the text of each WAV chunk: POST /transcribe on the same worker service the
 * API already asks for audio and PDFs. The worker answers one result per file, in order.
 */
export async function transcribeViaWorker(
  audioPaths: string[],
  { endpoint, fetchFn = fetch, timeoutMs = 10 * 60_000 }: WorkerOptions = {},
): Promise<TranscribedChunk[]> {
  const baseUrl = endpoint ?? process.env.YOUTUBE_WORKER_ENDPOINT ?? 'http://localhost:5000';

  let response: Response;
  try {
    response = await fetchFn(`${baseUrl}/transcribe`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ audioPaths, language: 'es-CO' }),
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (error) {
    throw new Error(
      `No se pudo contactar al worker de transcripción (¿está corriendo el servicio?): ${(error as Error).message}`,
    );
  }

  if (!response.ok) {
    const body = (await response.json().catch(() => ({}))) as { message?: string };
    throw new Error(body.message ?? `El worker de transcripción respondió ${response.status}.`);
  }

  const { chunks } = (await response.json()) as { chunks?: TranscribedChunk[] };
  if (!Array.isArray(chunks) || chunks.length !== audioPaths.length) {
    throw new Error(
      `El worker devolvió ${Array.isArray(chunks) ? chunks.length : 0} resultados para ${audioPaths.length} archivos.`,
    );
  }
  return chunks;
}

/**
 * The note's text from its chunks. Silence, music or audio that is not Spanish keep the placeholder the editor has
 * always shown; a chunk that failed shows "--error--" while the others still count; when every chunk failed there is
 * nothing to save, so it says why instead of saving a note full of "--error--".
 */
export function assembleTranscript(chunks: TranscribedChunk[]): string {
  const failures = chunks.filter((chunk) => chunk.status in FAILURE_MESSAGES);
  if (chunks.length > 0 && failures.length === chunks.length) {
    throw new Error(FAILURE_MESSAGES[failures[0].status]);
  }
  failures.forEach((chunk) => console.error('Error transcribing a chunk:', FAILURE_MESSAGES[chunk.status]));

  return chunks
    .map((chunk) => {
      if (chunk.status === 'recognized') return chunk.text;
      if (chunk.status === 'no_speech') return 'Música-NoEspañol.';
      return '--error--';
    })
    .join(' ');
}
