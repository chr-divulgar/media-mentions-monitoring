// Pure helpers of the TV video flow — kept apart from VideoService so they can be tested without
// a worker, a filesystem or ffmpeg.

/**
 * Where a segment window starts, in seconds from the start of the alert's hour file: centred on the
 * end of the alert (the same rule the audio flow uses), and never before the beginning of the file.
 */
export function segmentStartSeconds(
  alertEndSeconds: number,
  windowSeconds: number,
): number {
  return alertEndSeconds > windowSeconds / 2
    ? alertEndSeconds - windowSeconds / 2
    : 0;
}

/**
 * How far into a clip the selected start falls. A clip made by stream copy begins on a keyframe, a
 * little before the start that was asked for; the audio taken from it for the transcription is
 * trimmed by this much so the text covers exactly the selected window.
 */
export function audioOffsetInClip(clipStart: Date, selectedStart: Date): number {
  return Math.max(0, (selectedStart.getTime() - clipStart.getTime()) / 1000);
}

/**
 * Reads an HTTP Range header ("bytes=a-b", "bytes=a-", "bytes=-n") for a file of `size` bytes.
 * Returns null when there is no header (serve the whole file) and 'invalid' when the range cannot
 * be satisfied (answer 416). A <video> element needs Range support to seek.
 */
export function parseByteRange(
  header: string | undefined,
  size: number,
): { start: number; end: number } | 'invalid' | null {
  if (!header) return null;

  const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!match || (match[1] === '' && match[2] === '')) return 'invalid';

  let start: number;
  let end: number;
  if (match[1] === '') {
    // suffix: the last N bytes
    const suffix = Number(match[2]);
    if (suffix === 0) return 'invalid';
    start = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    start = Number(match[1]);
    end = match[2] === '' ? size - 1 : Math.min(Number(match[2]), size - 1);
  }

  if (start >= size || start > end) return 'invalid';
  return { start, end };
}

/**
 * What a note carries to WhatsApp besides its text: the video clip for a TV note that has one,
 * the mp3 for everything else (including a TV note edited through the audio fallback).
 */
export function noteMediaKind(
  media: string | undefined,
  hasVideoClip: boolean,
): 'video' | 'audio' {
  const key = (media ?? '').trim().toLowerCase();
  return (key === 'tv' || key === 'television') && hasVideoClip ? 'video' : 'audio';
}

/** Clip names come from the URL; only the characters the editor itself produces are allowed. */
export function isSafeClipName(name: string): boolean {
  return /^[A-Za-z0-9_-]+$/.test(name);
}
