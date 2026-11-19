import { TextModel, TextModelError } from './text-model';

interface FailoverOptions {
  now?: () => number;
  log?: (message: string) => void;
  /** How long a model that ran out of quota is left alone. */
  rateLimitCooldownMs?: number;
  /** How long a model that was unreachable is left alone. */
  unavailableCooldownMs?: number;
}

/**
 * Tries its models in order and moves on to the next when one fails: several keys of one provider, or one provider
 * after another. A model that hit its quota or was unreachable rests for a while (it is tried last, never skipped,
 * so a single key is not blocked by one bad moment); one with an invalid key is turned off until the API restarts.
 * A rejected request (bad model, blocked content) fails at once: no other key would accept it.
 */
export class FailoverTextModel implements TextModel {
  readonly label = 'failover';

  private readonly now: () => number;
  private readonly log: (message: string) => void;
  private readonly rateLimitCooldownMs: number;
  private readonly unavailableCooldownMs: number;
  private readonly restingUntil = new Map<TextModel, number>();
  private readonly turnedOff = new Set<TextModel>();

  constructor(
    private readonly models: TextModel[],
    options: FailoverOptions = {},
  ) {
    this.now = options.now ?? Date.now;
    this.log = options.log ?? ((message) => console.warn(message));
    this.rateLimitCooldownMs = options.rateLimitCooldownMs ?? 5 * 60_000;
    this.unavailableCooldownMs = options.unavailableCooldownMs ?? 30_000;
  }

  async complete(instructions: string, input: string): Promise<string> {
    const active = this.models.filter((model) => !this.turnedOff.has(model));
    if (active.length === 0) {
      throw new Error('No quedan modelos de IA disponibles: todas las claves fueron rechazadas (invalid key).');
    }

    const resting = (model: TextModel) => (this.restingUntil.get(model) ?? 0) > this.now();
    const order = [...active.filter((model) => !resting(model)), ...active.filter(resting)];

    const attempts: string[] = [];
    for (const model of order) {
      try {
        return await model.complete(instructions, input);
      } catch (error) {
        const failure = error instanceof TextModelError ? error : new TextModelError('unavailable', String(error));
        if (failure.kind === 'rejected') throw failure;

        attempts.push(`${model.label}: ${failure.kind}${failure.status ? ` (${failure.status})` : ''} — ${failure.message}`);
        this.rest(model, failure);
      }
    }

    throw new Error(`Ningún modelo de IA respondió. ${attempts.join(' | ')}`);
  }

  private rest(model: TextModel, failure: TextModelError): void {
    if (failure.kind === 'auth') {
      this.turnedOff.add(model);
      this.log(`[AI] ${model.label} rechazó su clave y queda desactivado hasta reiniciar la API.`);
    } else if (failure.kind === 'rate-limit') {
      this.restingUntil.set(model, this.now() + this.rateLimitCooldownMs);
    } else {
      this.restingUntil.set(model, this.now() + this.unavailableCooldownMs);
    }
  }
}
