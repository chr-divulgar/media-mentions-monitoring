import { Injectable, Logger } from '@nestjs/common';
import { WhatsAppStatusDto, WhatsAppQrDto, WhatsAppGroupsDto } from '@repo/shared';

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

  /**
   * Sends a text and/or an mp3 file to one recipient (phone number or group JID) through the
   * worker's Baileys session. Throws unless the sidecar confirms the send.
   */
  async send(number: string, message?: string, audioPath?: string): Promise<void> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';

    const response = await fetch(`${workerUrl}/whatsapp/send`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ number, message, audioPath }),
      // Above the worker's own 30s relay timeout.
      signal: AbortSignal.timeout(35000),
    });

    // The worker answers 200 { status: 'worker_unreachable' } when the sidecar is down, so a 2xx
    // alone is not proof of delivery — only the sidecar's own { success: true } is.
    const data = (await response.json().catch(() => ({}))) as { success?: boolean; message?: string };
    if (!response.ok || data.success !== true) {
      throw new Error(data.message ?? 'WhatsApp no disponible');
    }
  }

  async getGroups(): Promise<WhatsAppGroupsDto> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';

    try {
      // Above the worker's own 10s relay timeout (see /whatsapp/groups in YouTubeCookiesHttpService.cs).
      const response = await fetch(`${workerUrl}/whatsapp/groups`, {
        signal: AbortSignal.timeout(12000),
      });

      if (!response.ok) {
        return { groups: [] };
      }

      // The worker answers { status: 'worker_unreachable' } (no groups key) when the sidecar is down.
      const data = (await response.json()) as Partial<WhatsAppGroupsDto>;
      return { groups: data.groups ?? [] };
    } catch (error) {
      this.logger.warn(
        `[WhatsAppService] Worker groups fetch failed: ${error instanceof Error ? error.message : String(error)}`,
      );
      return { groups: [] };
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
