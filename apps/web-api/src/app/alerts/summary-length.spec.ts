import { summaryInstructions, summaryWordTarget } from './summary-length';

describe('summaryWordTarget', () => {
  it.each([
    [1, 20],
    [29, 20],
    [30, 30],
    [59, 30],
    [60, 40],
    [200, 40],
    [239, 40],
    [240, 50],
    [299, 50],
    [300, 60],
    [360, 70],
    [420, 80],
    [479, 80],
    [480, 90],
    [1200, 90],
  ])('a %i s audio gets a %i word summary', (durationSeconds, words) => {
    expect(summaryWordTarget({ durationSeconds })).toBe(words);
  });

  it('is 50 words for economic indicators, whatever the duration', () => {
    expect(summaryWordTarget({ durationSeconds: 20, economicIndicators: true })).toBe(50);
    expect(summaryWordTarget({ durationSeconds: 200, economicIndicators: true })).toBe(50);
    expect(summaryWordTarget({ durationSeconds: 900, economicIndicators: true })).toBe(50);
    expect(summaryWordTarget({ economicIndicators: true })).toBe(50);
  });

  it('is the usual 40 when the duration is not known', () => {
    expect(summaryWordTarget({})).toBe(40);
    expect(summaryWordTarget({ durationSeconds: 0 })).toBe(40);
    expect(summaryWordTarget({ durationSeconds: Number.NaN })).toBe(40);
  });
});

describe('summaryInstructions', () => {
  it('asks for the title on its own line and the summary as one paragraph of that many words', () => {
    const text = summaryInstructions(60);

    expect(text).toContain('Realiza un resumen corto con título de 60 palabras en un solo párrafo de manera coherente y bien redactado del siguiente audio');
    expect(text).toMatch(/primera línea/i);
    expect(text).toMatch(/60 palabras como máximo/);
    expect(text).toMatch(/fiel a los hechos/i);
  });
});
