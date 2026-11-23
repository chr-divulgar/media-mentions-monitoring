/**
 * How many words the summary of an audio note has, by how long the audio is (what the analysts always wrote by hand):
 * under 30 s, 20 words; under a minute, 30; one to four minutes, 40; from four minutes, 50 and 10 more for each
 * further minute, up to 90. Economic indicators are always 50. Without a duration, the usual 40.
 */
export function summaryWordTarget(input: { durationSeconds?: number; economicIndicators?: boolean }): number {
  if (input.economicIndicators) return 50;

  const seconds = input.durationSeconds;
  if (seconds === undefined || !Number.isFinite(seconds) || seconds <= 0) return 40;
  if (seconds < 30) return 20;
  if (seconds < 60) return 30;
  if (seconds < 240) return 40;
  return Math.min(90, 50 + (Math.floor(seconds / 60) - 4) * 10);
}

/**
 * What the AI is asked. The first sentence is the analysts' own. The title goes alone on the first line because the
 * summary is split from it by lines (see AlertsService.getChatResponse).
 */
export function summaryInstructions(words: number): string {
  return [
    `Realiza un resumen corto con título de ${words} palabras en un solo párrafo de manera coherente y bien redactado del siguiente audio.`,
    `Escribe el título solo en la primera línea y, después, el resumen en un solo párrafo de ${words} palabras como máximo.`,
    'Sé fiel a los hechos y no atribuyas acciones o eventos a personas o entidades que no los hicieron.',
  ].join('\n');
}
