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

export type CaptureIncidentKind =
  | 'failed'
  | 'retry-failed'
  | 'recovered'
  | 'excluded'
  | 'startup-failed'
  | 'worker-started'
  | 'worker-stopped';

// One entry of the incident timeline of an hour: why a source stopped recording and what the
// worker did about it. Runs of identical failed retries arrive already collapsed (until + count).
export interface CaptureIncidentDto {
  readonly at: string; // ISO-8601 in Bogotá time: HH:mm can be read straight from the text
  readonly until?: string | null;
  readonly kind: CaptureIncidentKind;
  readonly reason?: string | null;
  readonly count: number;
}

export interface CaptureHourStatusDto {
  readonly hour: number;
  readonly status: CaptureHourStatus;
  readonly coveragePercent?: number | null;
  // Sub-hour timeline, present when checkpoint data was collected for this hour (see the worker's
  // CaptureSegmentBuilder) — absent for hours evidenced before this existed, or with no data at all.
  readonly segments?: CaptureSegmentDto[] | null;
  readonly incidents?: CaptureIncidentDto[] | null;
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
