import { VideoService } from './video.service';

const FILE = 'D:/capture/rcn_tv_2026-11-10_15-00-00.opus';
// The file starts at 15:00 Bogotá (20:00 UTC); the alert ends 20 minutes into it.
const alert = { source: 'worker' as const, endTime: '2026-11-10T20:20:00.000Z', filePath: FILE };

function build() {
  const service = new VideoService();
  const fetchClip = jest.spyOn(service as never, 'fetchClip' as never).mockImplementation((async (
    _sourceId: string,
    startUtc: Date,
    endUtc: Date,
  ) => ({ startUtc, durationSeconds: (endUtc.getTime() - startUtc.getTime()) / 1000 })) as never);
  return { service, fetchClip: fetchClip as unknown as jest.Mock };
}

// (sourceId, startUtc, endUtc, outputPath, exact)
const windowOf = (fetchClip: jest.Mock) => ({
  start: fetchClip.mock.calls[0][1].toISOString(),
  end: fetchClip.mock.calls[0][2].toISOString(),
});

describe('VideoService.createVideoFile: the window of a segment', () => {
  it('without extension, centres the window on the end of the alert, as always', async () => {
    const { service, fetchClip } = build();

    await service.createVideoFile({ alert, duration: 1800, output: 'segment_a1_30' });

    expect(windowOf(fetchClip)).toEqual({ start: '2026-11-10T20:05:00.000Z', end: '2026-11-10T20:35:00.000Z' });
  });

  it('with 10 more minutes before, starts earlier and keeps the end', async () => {
    const { service, fetchClip } = build();

    const result = await service.createVideoFile({
      alert,
      duration: 2400,
      before: 1500,
      after: 900,
      output: 'segment_a1_b25_a15',
    });

    expect(windowOf(fetchClip)).toEqual({ start: '2026-11-10T19:55:00.000Z', end: '2026-11-10T20:35:00.000Z' });
    expect(result.startSeconds).toBe(-300);
  });

  it('with 10 more minutes after, ends later and keeps the start', async () => {
    const { service, fetchClip } = build();

    await service.createVideoFile({ alert, duration: 2400, before: 900, after: 1500, output: 'segment_a1_b15_a25' });

    expect(windowOf(fetchClip)).toEqual({ start: '2026-11-10T20:05:00.000Z', end: '2026-11-10T20:45:00.000Z' });
  });
});
