import { kindFromStatus, keyTail, TextModel, TextModelError } from './text-model';

const BASE_URL = 'https://generativelanguage.googleapis.com/v1beta/models';

/**
 * Gemini through its own REST API, with one key. Several keys are several instances behind a FailoverTextModel.
 * The key travels in a header, never in the address, so it cannot end up in a log of requests.
 */
export class GeminiTextModel implements TextModel {
  readonly label: string;

  constructor(
    private readonly apiKey: string,
    private readonly model: string,
    private readonly fetchFn: typeof fetch = fetch,
    private readonly timeoutMs = 30_000,
  ) {
    this.label = `gemini ${keyTail(apiKey)}`;
  }

  async complete(instructions: string, input: string): Promise<string> {
    let response: Response;
    try {
      response = await this.fetchFn(`${BASE_URL}/${encodeURIComponent(this.model.replace(/^models\//, ''))}:generateContent`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'x-goog-api-key': this.apiKey },
        body: JSON.stringify({
          systemInstruction: { parts: [{ text: instructions }] },
          contents: [{ role: 'user', parts: [{ text: input }] }],
        }),
        signal: AbortSignal.timeout(this.timeoutMs),
      });
    } catch (error) {
      throw new TextModelError('unavailable', `sin respuesta de Gemini: ${(error as Error).message}`);
    }

    if (!response.ok) {
      const body = await response.text().catch(() => '');
      // Google answers a wrong key with a 400, not a 401: it must still turn the key off, not fail the request.
      const kind = /API_KEY_INVALID|API key not valid|API key expired/i.test(body) ? 'auth' : kindFromStatus(response.status);
      throw new TextModelError(kind, `Gemini respondió ${response.status}: ${messageOf(body)}`, response.status);
    }

    const data = (await response.json()) as GeminiResponse;
    const text = data.candidates?.[0]?.content?.parts?.map((part) => part.text ?? '').join('').trim();
    if (!text) {
      const why = data.promptFeedback?.blockReason ?? data.candidates?.[0]?.finishReason ?? 'sin texto';
      throw new TextModelError('rejected', `Gemini no devolvió texto (${why}).`);
    }

    return text;
  }
}

interface GeminiResponse {
  candidates?: { content?: { parts?: { text?: string }[] }; finishReason?: string }[];
  promptFeedback?: { blockReason?: string };
}

// The error's own message, short, when the body is Google's JSON error.
function messageOf(body: string): string {
  try {
    const message = (JSON.parse(body) as { error?: { message?: string } }).error?.message;
    if (message) return message.slice(0, 200);
  } catch {
    // not JSON: fall through
  }
  return body.slice(0, 200);
}
