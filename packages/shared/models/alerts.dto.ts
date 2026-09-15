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
  // Which collection this alert was read from (see GetAlertsDto.source). Stamped
  // by AlertsService.getAlerts so the audio-cut flow knows whether endTime/startTime
  // are real UTC (worker) or the legacy mislabeled-local-time quirk.
  readonly source?: 'legacy' | 'worker';
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
