import OpenAI from 'openai';
import { kindFromStatus, TextModel, TextModelError } from './text-model';

/** OpenAI (Responses API) with one key. */
export class OpenAiTextModel implements TextModel {
  readonly label = 'openai';
  private readonly client: OpenAI;

  constructor(
    apiKey: string,
    private readonly model: string,
  ) {
    this.client = new OpenAI({ apiKey });
  }

  async complete(instructions: string, input: string): Promise<string> {
    let text: string;
    try {
      text = (await this.client.responses.create({ model: this.model, instructions, input })).output_text;
    } catch (error) {
      const status = (error as { status?: number }).status;
      // No status: the request never got an answer (network, timeout).
      throw new TextModelError(status ? kindFromStatus(status) : 'unavailable', `OpenAI: ${(error as Error).message}`, status);
    }

    if (!text?.trim()) throw new TextModelError('rejected', 'OpenAI no devolvió texto.');
    return text;
  }
}
