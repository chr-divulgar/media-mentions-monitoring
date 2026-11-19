import { Dayjs } from 'dayjs';

export class AlertDto {
  readonly id?: string;
  readonly text?: string;
  readonly startTime?: string;
  readonly endTime?: string;
  readonly media?: string;
  readonly words?: string[];
  readonly filePath?: string;
  readonly platform?: string;
  readonly clientName?: string;
  readonly type?: string;
  // Only set for web-news mentions (media === 'internet'); audio alerts leave these undefined.
  readonly title?: string;
  readonly url?: string;
  // Which collection this alert was read from (see GetAlertsDto.source). Stamped
  // by AlertsService.getAlerts so the audio-cut flow knows whether endTime/startTime
  // are real UTC (worker) or the legacy mislabeled-local-time quirk.
  readonly source?: 'legacy' | 'worker';
  /** N1..N4 (worker alerts only). */
  readonly level?: string;
  /** Seconds from capture to the first confirmed delivery (worker alerts, RQ-031); absent until sent. */
  readonly slaSeconds?: number;
  /** When the worker caught it (worker alerts): a web note can arrive hours after it was published. */
  readonly capturedAt?: string;
  /** For a repeat, the id of the "Nueva" alert that started its story (worker alerts); absent on older alerts. */
  readonly repeatOfAlertId?: string;
  /** Web mention the worker could not read or date: only title and link are stored, and this says what to check. */
  readonly reviewReason?: string;
  /** What has happened with this alert on the portal (worker alerts): see ALERT_REVIEW_STATUSES. */
  readonly reviewStatus?: AlertReviewStatus;
  /** When someone first opened the alert's editor, if anyone has. */
  readonly viewedAt?: string;
  /** The note made from this alert, if there is one. */
  readonly noteId?: string;
}

/** Where an alert is in its handling, from not looked at to its note sent. The most advanced applies. */
export type AlertReviewStatus = 'unseen' | 'viewed' | 'draft' | 'saved' | 'sent';

/**
 * The states, in order of progress, with the color the alerts table shows and what each one means. The single source
 * of the status column and of its legend, so they cannot disagree.
 */
export const ALERT_REVIEW_STATUSES: readonly {
  readonly value: AlertReviewStatus;
  readonly label: string;
  readonly color: string;
  readonly description: string;
}[] = [
  { value: 'unseen', label: 'Sin ver', color: '#f5222d', description: 'Nadie ha abierto la alerta y no tiene nota.' },
  { value: 'viewed', label: 'Vista', color: '#faad14', description: 'Alguien abrió el editor, pero todavía no hay nota.' },
  { value: 'draft', label: 'Nota en borrador', color: '#1677ff', description: 'Ya se transcribió, pero falta guardar la nota.' },
  { value: 'saved', label: 'Nota guardada', color: '#52c41a', description: 'La nota está guardada con su titular.' },
  { value: 'sent', label: 'Nota enviada', color: '#722ed1', description: 'La nota ya se envió por WhatsApp.' },
];

/** Target for "alert within 5 minutes of capture" (AMB-001); configurable per level later. */
export const ALERT_SLA_TARGET_SECONDS = 300;

export class AlertSlaSummaryDto {
  /** Worker alerts in the range that were actually delivered (have a measured SLA). */
  readonly measured!: number;
  readonly withinTarget!: number;
  readonly targetSeconds!: number;
  readonly p50Seconds!: number | null;
  readonly p95Seconds!: number | null;
}

export class GetAlertsDto {
  readonly startDate?: string;
  readonly endDate?: string;
  readonly madia?: string;
  readonly clientName?: string;
  readonly platform?: string;
  readonly type?: string[];
  // 'worker' reads the shadow-run apps/media-core-worker alerts (monitoring.workerAlert) instead of
  // the legacy monitoring.alert collection. Omitted or 'legacy' preserves today's behavior.
  readonly source?: 'legacy' | 'worker';
}

export class GetTranscriptionDto {
  readonly filename?: string;
}
export class TranscriptionDto {
  readonly noteId?: string;
  readonly text?: string;
}
export class ValidDatesDto {
  readonly minDate?: string;
  readonly maxDate?: string;
}

export class GetSummaryDto {
  readonly noteId?: string;
  readonly text?: string;
  readonly words?: string[];
}

export class SummaryDto {
  readonly title?: string;
  readonly summary?: string;
}

export type DateRange = [Dayjs | null, Dayjs | null];

export const dateFormat = 'YYYY-MM-DD';
