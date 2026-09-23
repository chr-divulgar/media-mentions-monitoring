import { Injectable, Logger } from '@nestjs/common';
import { CaptureStatusResponseDto, GetCaptureStatusDto } from '@repo/shared';

@Injectable()
export class StatusService {
  private readonly logger = new Logger(StatusService.name);

  /**
   * Fetches per-source, per-hour capture coverage for one day from the .NET worker's
   * GET /capture/status endpoint. Same worker-HTTP pattern as YouTubeService.checkWorkerHealth —
   * no filesystem fallback, since the worker is the only process that knows its own live state.
   */
  async getCaptureStatus(dto: GetCaptureStatusDto): Promise<CaptureStatusResponseDto> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const query = dto.date ? `?date=${encodeURIComponent(dto.date)}` : '';

    try {
      const response = await fetch(`${workerUrl}/capture/status${query}`, {
        signal: AbortSignal.timeout(5000),
      });

      if (!response.ok) {
        throw new Error(`Worker responded with status ${response.status}`);
      }

      return (await response.json()) as CaptureStatusResponseDto;
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.warn(`[StatusService] Worker unreachable: ${errorMsg}`);
      throw new Error(`Worker is unreachable: ${errorMsg}`);
    }
  }
}
