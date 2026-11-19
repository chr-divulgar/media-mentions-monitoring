import { FailoverTextModel } from './failover-text-model';
import { GeminiTextModel } from './gemini-text-model';
import { OpenAiTextModel } from './openai-text-model';
import { TextModel } from './text-model';

// "-latest" aliases follow the current Flash model, so the default does not go stale when Google retires a version.
const DEFAULT_GEMINI_MODEL = 'gemini-flash-latest';
const DEFAULT_OPENAI_MODEL = 'gpt-5-mini';

type Env = (name: string) => string | undefined;

/**
 * The models the environment asks for, in order:
 *   AI_PROVIDERS   "gemini", "openai" or a list such as "gemini,openai" (default "openai")
 *   GEMINI_API_KEYS  one or several keys, separated by commas: each one is its own entry in the chain
 *   GEMINI_MODEL / OPENAI_MODEL / OPEN_AI_KEY
 * Anything missing fails here, at startup, not at the first summary.
 */
export function buildTextModels(env: Env): TextModel[] {
  const providers = (env('AI_PROVIDERS') ?? 'openai')
    .split(',')
    .map((name) => name.trim().toLowerCase())
    .filter(Boolean);

  const models: TextModel[] = [];
  for (const provider of providers) {
    if (provider === 'gemini') {
      const keys = (env('GEMINI_API_KEYS') ?? env('GEMINI_API_KEY') ?? '')
        .split(',')
        .map((key) => key.trim())
        .filter(Boolean);
      if (keys.length === 0) throw new Error('AI_PROVIDERS incluye "gemini" pero falta GEMINI_API_KEYS (una o varias claves separadas por comas).');
      const model = env('GEMINI_MODEL')?.trim() || DEFAULT_GEMINI_MODEL;
      models.push(...keys.map((key) => new GeminiTextModel(key, model)));
    } else if (provider === 'openai') {
      const key = env('OPEN_AI_KEY')?.trim();
      if (!key) throw new Error('AI_PROVIDERS incluye "openai" pero falta OPEN_AI_KEY.');
      models.push(new OpenAiTextModel(key, env('OPENAI_MODEL')?.trim() || DEFAULT_OPENAI_MODEL));
    } else {
      throw new Error(`AI_PROVIDERS tiene un proveedor desconocido: "${provider}" (usa "gemini" u "openai").`);
    }
  }

  if (models.length === 0) throw new Error('AI_PROVIDERS está vacío.');
  return models;
}

/** The text model of the API: the configured models behind one failover. */
export function buildTextModel(env: Env): TextModel {
  return new FailoverTextModel(buildTextModels(env));
}
