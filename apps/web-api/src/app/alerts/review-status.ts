import { AlertReviewStatus } from '@repo/shared';

/** What the status needs to know of the note made from an alert. */
interface NoteState {
  text?: string | null;
  title?: string | null;
  sentAt?: Date | string | null;
}

/**
 * Where an alert is in its handling. The most advanced applies, so an alert with a note is never "unseen" (notes made
 * before views were recorded) and one whose note was deleted goes back to "viewed".
 */
export function reviewStatusOf(input: {
  viewedAt?: Date | string | null;
  note?: NoteState | null;
}): AlertReviewStatus {
  const { viewedAt, note } = input;
  if (note?.sentAt) return 'sent';
  if (note?.title?.trim()) return 'saved';
  if (note) return 'draft';
  return viewedAt ? 'viewed' : 'unseen';
}
