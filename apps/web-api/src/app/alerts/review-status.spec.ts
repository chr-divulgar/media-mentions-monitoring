import { ALERT_REVIEW_STATUSES } from '@repo/shared';
import { reviewStatusOf } from './review-status';

describe('reviewStatusOf', () => {
  it('is "unseen" when nobody opened the alert and there is no note', () => {
    expect(reviewStatusOf({})).toBe('unseen');
  });

  it('is "viewed" once someone opened it', () => {
    expect(reviewStatusOf({ viewedAt: new Date() })).toBe('viewed');
  });

  it('is "draft" when the note exists but has no title yet (it was only transcribed)', () => {
    expect(reviewStatusOf({ viewedAt: new Date(), note: { text: 'transcripción' } })).toBe('draft');
    expect(reviewStatusOf({ note: { title: '   ' } })).toBe('draft');
  });

  it('is "saved" when the note has its title', () => {
    expect(reviewStatusOf({ viewedAt: new Date(), note: { title: 'Ecopetrol coloca bonos' } })).toBe('saved');
  });

  it('is "sent" when the note was sent, the most advanced state', () => {
    expect(reviewStatusOf({ note: { title: 'Ecopetrol coloca bonos', sentAt: new Date() } })).toBe('sent');
  });

  it('never shows an alert with a note as unseen, even if nobody opened it with this feature', () => {
    expect(reviewStatusOf({ note: { title: 'Nota de antes' } })).toBe('saved');
  });

  it('goes back to "viewed" when the note is deleted', () => {
    expect(reviewStatusOf({ viewedAt: new Date(), note: null })).toBe('viewed');
  });
});

describe('ALERT_REVIEW_STATUSES', () => {
  it('lists the five states in order of progress, each with its own color', () => {
    expect(ALERT_REVIEW_STATUSES.map((status) => status.value)).toEqual(['unseen', 'viewed', 'draft', 'saved', 'sent']);
    expect(new Set(ALERT_REVIEW_STATUSES.map((status) => status.color)).size).toBe(5);
    expect(ALERT_REVIEW_STATUSES.every((status) => status.label && status.description)).toBe(true);
  });
});
