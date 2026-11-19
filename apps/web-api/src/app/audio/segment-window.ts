import { MAX_SEGMENT_WINDOW_SECONDS } from '@repo/shared';

/**
 * The window the editor asked for with its "+10 min" buttons: seconds of audio before and after the end of the alert.
 * Null when it did not extend anything, and the usual window centred on the end of the alert applies.
 */
export function requestedWindow(dto: {
  before?: number;
  after?: number;
  duration?: number;
}): { before: number; after: number } | null {
  if (dto.before === undefined && dto.after === undefined) return null;

  const half = (dto.duration ?? 0) / 2;
  const before = dto.before ?? half;
  const after = dto.after ?? half;
  const sides = [before, after];
  if (
    !sides.every((side) => Number.isFinite(side) && side >= 0) ||
    before + after <= 0 ||
    before + after > MAX_SEGMENT_WINDOW_SECONDS
  ) {
    throw new Error(
      `Ventana de audio no válida: debe ser mayor que 0 y de máximo ${MAX_SEGMENT_WINDOW_SECONDS / 3600} horas.`,
    );
  }
  return { before, after };
}
