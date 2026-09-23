export interface GetCaptureStatusDto {
  readonly date?: string; // yyyy-MM-dd, defaults to today (Bogotá) when omitted
}

export type CaptureHourStatus = 'ok' | 'gap-filled' | 'no-session' | 'excluded' | 'no-data' | 'live';

export type CaptureSegmentState = 'captured' | 'gap' | 'no-session';

export interface CaptureSegmentDto {
  readonly startSeconds: number;
  readonly endSeconds: number;
  readonly state: CaptureSegmentState;
}

export interface CaptureHourStatusDto {
  readonly hour: number;
  readonly status: CaptureHourStatus;
  readonly coveragePercent?: number | null;
  // Sub-hour timeline, present when checkpoint data was collected for this hour (see the worker's
  // CaptureSegmentBuilder) — absent for hours evidenced before this existed, or with no data at all.
  readonly segments?: CaptureSegmentDto[] | null;
}

export interface CaptureSourceStatusDto {
  readonly sourceId: string;
  readonly platform?: string;
  readonly media?: string;
  readonly isExcluded: boolean;
  readonly hours: CaptureHourStatusDto[];
}

export interface CaptureStatusResponseDto {
  readonly date: string;
  readonly sources: CaptureSourceStatusDto[];
}
