import { AudioService } from './audio.service';

const FILE = 'D:/capture/caracol_2026-11-10_15-00-00.opus';
// The file starts at 15:00 Bogotá; the alert ends 20 minutes into it.
const alert = { source: 'worker' as const, endTime: '2026-11-10T20:20:00.000Z', filePath: FILE };

function build() {
  const service = new AudioService();
  const extract = jest.spyOn(service as never, 'extractForWindowFromWorker' as never).mockResolvedValue(2400 as never);
  jest.spyOn(service as never, 'tryReuseCachedFile' as never).mockResolvedValue(null as never);
  return { service, extract: extract as unknown as jest.Mock };
}

// (sourceId, fileTime, windowStart, windowEnd, bitrate, frequency, outputPath, format)
const windowOf = (extract: jest.Mock) => ({ start: extract.mock.calls[0][2], end: extract.mock.calls[0][3] });

describe('AudioService.createAudioFile: the window of a segment', () => {
  it('without extension, centres the window on the end of the alert, as always', async () => {
    const { service, extract } = build();

    const result = await service.createAudioFile({ alert, duration: 1800, output: 'segment_a1_30' });

    // The alert ends at second 1200: 900 s each side.
    expect(windowOf(extract)).toEqual({ start: 300, end: 2100 });
    expect(result.startSeconds).toBe(300);
  });

  it('without extension, still starts at the beginning of the file when the alert is near it', async () => {
    const { service, extract } = build();

    await service.createAudioFile({ alert, duration: 3600, output: 'segment_a1_60' });

    // 1200 s is less than half of 3600: the window starts at the file start, as before.
    expect(windowOf(extract)).toEqual({ start: 0, end: 3600 });
  });

  it('with 10 more minutes before, starts earlier and keeps the end', async () => {
    const { service, extract } = build();

    const result = await service.createAudioFile({
      alert,
      duration: 2400,
      before: 1500,
      after: 900,
      output: 'segment_a1_b25_a15',
    });

    expect(windowOf(extract)).toEqual({ start: -300, end: 2100 });
    expect(result.startSeconds).toBe(-300); // negative: it reaches into the hour before
  });

  it('with 10 more minutes after, ends later and keeps the start', async () => {
    const { service, extract } = build();

    await service.createAudioFile({ alert, duration: 2400, before: 900, after: 1500, output: 'segment_a1_b15_a25' });

    expect(windowOf(extract)).toEqual({ start: 300, end: 2700 });
  });

  it('refuses a window of more than 4 hours', async () => {
    const { service, extract } = build();

    await expect(
      service.createAudioFile({ alert, duration: 20000, before: 10000, after: 10000, output: 'segment_a1_b1_a1' }),
    ).rejects.toThrow(/ventana/i);
    expect(extract).not.toHaveBeenCalled();
  });
});
