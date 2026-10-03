export type WhatsAppConnectionStatus = 'connected' | 'pending_qr' | 'disconnected' | 'worker_unreachable';

export class WhatsAppStatusDto {
  readonly status!: WhatsAppConnectionStatus;
}

export class WhatsAppQrDto {
  /** Base64 data URL (data:image/png;base64,...) of the current pairing QR, or null. */
  readonly qr!: string | null;
}

export class WhatsAppGroupDto {
  /** WhatsApp group JID ("<id>@g.us") — stored as-is as an alert recipient. */
  readonly id!: string;
  readonly name!: string;
}

export class WhatsAppGroupsDto {
  /** Empty when the session is not connected or the worker is unreachable. */
  readonly groups!: WhatsAppGroupDto[];
}
