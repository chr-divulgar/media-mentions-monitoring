import { requestedWindow } from './segment-window';

describe('requestedWindow', () => {
  it('is null when the editor did not extend the window: the usual centred one applies', () => {
    expect(requestedWindow({ duration: 3600 })).toBeNull();
    expect(requestedWindow({})).toBeNull();
  });

  it('is the seconds before and after the end of the alert the editor asked for', () => {
    expect(requestedWindow({ before: 1500, after: 900 })).toEqual({ before: 1500, after: 900 });
  });

  it('takes half of the duration for the side that was not given', () => {
    expect(requestedWindow({ before: 1500, duration: 1800 })).toEqual({ before: 1500, after: 900 });
  });

  it('accepts a window up to 4 hours and no more', () => {
    expect(requestedWindow({ before: 7200, after: 7200 })).toEqual({ before: 7200, after: 7200 });
    expect(() => requestedWindow({ before: 7200, after: 7201 })).toThrow(/ventana/i);
  });

  it.each([
    [{ before: -1, after: 900 }],
    [{ before: 900, after: Number.NaN }],
    [{ before: 0, after: 0 }],
    [{ before: Infinity, after: 900 }],
  ])('refuses a window that makes no sense (%j)', (window) => {
    expect(() => requestedWindow(window)).toThrow(/ventana/i);
  });
});
