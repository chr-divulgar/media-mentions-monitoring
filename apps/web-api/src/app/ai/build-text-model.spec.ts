import { buildTextModels } from './build-text-model';

const env = (values: Record<string, string>) => (name: string) => values[name];

describe('buildTextModels', () => {
  it('defaults to OpenAI, so nothing changes until the environment says otherwise', () => {
    const models = buildTextModels(env({ OPEN_AI_KEY: 'sk-test' }));

    expect(models.map((m) => m.label)).toEqual(['openai']);
  });

  it('makes one Gemini model per key, in the order written', () => {
    const models = buildTextModels(env({ AI_PROVIDERS: 'gemini', GEMINI_API_KEYS: 'AAAA1111, BBBB2222 ,CCCC3333' }));

    expect(models.map((m) => m.label)).toEqual(['gemini …1111', 'gemini …2222', 'gemini …3333']);
  });

  it('keeps the order of the providers: Gemini first, OpenAI as the last resort', () => {
    const models = buildTextModels(env({ AI_PROVIDERS: 'gemini,openai', GEMINI_API_KEYS: 'AAAA1111', OPEN_AI_KEY: 'sk-test' }));

    expect(models.map((m) => m.label)).toEqual(['gemini …1111', 'openai']);
  });

  it('accepts a single GEMINI_API_KEY too', () => {
    const models = buildTextModels(env({ AI_PROVIDERS: 'gemini', GEMINI_API_KEY: 'AAAA1111' }));

    expect(models).toHaveLength(1);
  });

  it('fails at startup, saying what is missing, instead of at the first summary', () => {
    expect(() => buildTextModels(env({ AI_PROVIDERS: 'gemini' }))).toThrow(/GEMINI_API_KEYS/);
    expect(() => buildTextModels(env({}))).toThrow(/OPEN_AI_KEY/);
    expect(() => buildTextModels(env({ AI_PROVIDERS: 'claude', OPEN_AI_KEY: 'x' }))).toThrow(/claude/);
  });
});
