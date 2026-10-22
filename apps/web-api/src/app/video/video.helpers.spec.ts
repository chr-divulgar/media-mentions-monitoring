import {
  audioOffsetInClip,
  isSafeClipName,
  noteMediaKind,
  parseByteRange,
  segmentQuery,
  segmentStartSeconds,
} from './video.helpers';

describe('segmentStartSeconds', () => {
  it('centres the window on the end of the alert', () => {
    // alert ends 20 min into the hour, 10 min window: starts at minute 15
    expect(segmentStartSeconds(1200, 600)).toBe(900);
  });

  it('may start before the hour file: the worker brings in the recording before it, as the audio does', () => {
    // alert ends 2 min into the hour, 30 min window: starts 13 min before the hour
    expect(segmentStartSeconds(120, 1800)).toBe(-780);
    expect(segmentStartSeconds(-30, 600)).toBe(-330);
  });
});

describe('segmentQuery', () => {
  const start = new Date('2026-10-05T17:00:10.000Z');
  const end = new Date('2026-10-05T17:00:20.000Z');

  it('asks for the window as it is by default (a copy from the keyframe)', () => {
    const query = segmentQuery('rcn_tv', start, end, false);

    expect(Object.fromEntries(query)).toEqual({
      sourceId: 'rcn_tv',
      startUtc: '2026-10-05T17:00:10.000Z',
      endUtc: '2026-10-05T17:00:20.000Z',
    });
  });

  it('asks for the exact start and end for the clip a note keeps', () => {
    expect(segmentQuery('rcn_tv', start, end, true).get('exact')).toBe('1');
  });
});

describe('audioOffsetInClip', () => {
  it('is how far into the clip the selected start falls', () => {
    const clipStart = new Date('2026-10-05T17:00:10.000Z');
    const selectedStart = new Date('2026-10-05T17:00:13.500Z');
    expect(audioOffsetInClip(clipStart, selectedStart)).toBeCloseTo(3.5);
  });

  it('is zero when the clip starts after the selected start', () => {
    const clipStart = new Date('2026-10-05T17:00:10.000Z');
    expect(audioOffsetInClip(clipStart, new Date('2026-10-05T17:00:08.000Z'))).toBe(0);
  });
});

describe('parseByteRange', () => {
  const size = 1000;

  it('returns null when there is no Range header (serve the whole file)', () => {
    expect(parseByteRange(undefined, size)).toBeNull();
    expect(parseByteRange('', size)).toBeNull();
  });

  it('reads an explicit range', () => {
    expect(parseByteRange('bytes=0-499', size)).toEqual({ start: 0, end: 499 });
    expect(parseByteRange('bytes=200-299', size)).toEqual({ start: 200, end: 299 });
  });

  it('reads an open-ended range up to the last byte', () => {
    expect(parseByteRange('bytes=500-', size)).toEqual({ start: 500, end: 999 });
  });

  it('reads a suffix range as the last N bytes', () => {
    expect(parseByteRange('bytes=-100', size)).toEqual({ start: 900, end: 999 });
  });

  it('clamps an end past the file size', () => {
    expect(parseByteRange('bytes=900-5000', size)).toEqual({ start: 900, end: 999 });
  });

  it('flags a range that cannot be satisfied', () => {
    expect(parseByteRange('bytes=1000-1100', size)).toBe('invalid');
    expect(parseByteRange('bytes=500-100', size)).toBe('invalid');
    expect(parseByteRange('items=0-10', size)).toBe('invalid');
    expect(parseByteRange('bytes=abc-def', size)).toBe('invalid');
  });
});

describe('noteMediaKind', () => {
  it('sends the video clip for a TV note that has one', () => {
    expect(noteMediaKind('tv', true)).toBe('video');
    expect(noteMediaKind('television', true)).toBe('video');
    expect(noteMediaKind('TV', true)).toBe('video');
  });

  it('keeps the audio for a TV note made through the audio fallback (no video clip)', () => {
    expect(noteMediaKind('tv', false)).toBe('audio');
  });

  it('keeps the audio for every other media, whatever files exist', () => {
    expect(noteMediaKind('radio', true)).toBe('audio');
    expect(noteMediaKind(undefined, true)).toBe('audio');
  });
});

describe('isSafeClipName', () => {
  it('accepts the names the editor uses', () => {
    expect(isSafeClipName('segment_6700ab12_10')).toBe(true);
    expect(isSafeClipName('fragment_6700ab12')).toBe(true);
  });

  it('rejects anything that could leave the audioFiles folder', () => {
    expect(isSafeClipName('../secret')).toBe(false);
    expect(isSafeClipName('a/b')).toBe(false);
    expect(isSafeClipName('a\\b')).toBe(false);
    expect(isSafeClipName('name.mp4')).toBe(false);
    expect(isSafeClipName('')).toBe(false);
  });
});
