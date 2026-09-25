import { Injectable, Logger } from '@nestjs/common';
import { WhatsAppStatusDto, WhatsAppQrDto } from '@repo/shared';

// Mirrors the worker's WhatsAppUnreachableResponse / sidecar relay shape
// (GET /whatsapp/status, GET /whatsapp/qr — see YouTubeCookiesHttpService.cs).
type WorkerStatusPayload = { status: 'connected' | 'pending_qr' | 'disconnected' | 'worker_unreachable' };
type WorkerQrPayload = { qr: string | null };

@Injectable()
export class WhatsAppService {
  private readonly logger = new Logger(WhatsAppService.name);

  /**
   * The worker is the single source of truth, same as YouTubeService.getYouTubeStatus: nothing
   * is cached or stored locally in web-api, every call is a fresh proxy to the worker's
   * /whatsapp/status, which itself relays the Node/Baileys sidecar's own /status.
   */
  async getStatus(): Promise<WhatsAppStatusDto> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';

    try {
      // Kept above the worker's own timeout to the sidecar (4s, see RelayToSidecarAsync in
      // YouTubeCookiesHttpService.cs) so a normal login-time event-loop stall in the sidecar
      // doesn't get reported here as "worker unreachable".
      const response = await fetch(`${workerUrl}/whatsapp/status`, {
        signal: AbortSignal.timeout(6000),
      });

      if (!response.ok) {
        return { status: 'worker_unreachable' };
      }

      const data = (await response.json()) as WorkerStatusPayload;
      return { status: data.status };
    } catch (error) {
      this.logger.warn(
        `[WhatsAppService] Worker status check failed: ${error instanceof Error ? error.message : String(error)}`,
      );
      return { status: 'worker_unreachable' };
    }
  }

  async getQr(): Promise<WhatsAppQrDto> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';

    try {
      const response = await fetch(`${workerUrl}/whatsapp/qr`, {
        signal: AbortSignal.timeout(6000),
      });

      if (!response.ok) {
        return { qr: null };
      }

      const data = (await response.json()) as WorkerQrPayload;
      return { qr: data.qr };
    } catch (error) {
      this.logger.warn(
        `[WhatsAppService] Worker QR fetch failed: ${error instanceof Error ? error.message : String(error)}`,
      );
      return { qr: null };
    }
  }
}
