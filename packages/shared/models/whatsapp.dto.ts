export type WhatsAppConnectionStatus = 'connected' | 'pending_qr' | 'disconnected' | 'worker_unreachable';

export class WhatsAppStatusDto {
  readonly status!: WhatsAppConnectionStatus;
}

export class WhatsAppQrDto {
  /** Base64 data URL (data:image/png;base64,...) of the current pairing QR, or null. */
  readonly qr!: string | null;
}
