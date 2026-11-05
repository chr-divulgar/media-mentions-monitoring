export class WordAddDto {
  readonly before?: string;
  readonly after?: string;
}

/** N1 informativa · N2 seguimiento · N3 riesgo · N4 crisis (RQ-034). Missing means N1. */
export type AlertLevelLabel = 'N1' | 'N2' | 'N3' | 'N4';

export class WordDto {
  readonly value!: string;
  readonly adds!: WordAddDto[];
  /** Severity given to any alert this keyword triggers; the highest matched keyword wins. */
  readonly level?: AlertLevelLabel;
}

export class TopicItemDto {
  readonly id?: string;
  readonly name!: string;
  readonly subtopics?: SubtopicItemDto[];
}

export class SubtopicItemDto {
  readonly id?: string;
  readonly name!: string;
  readonly subsubtopics?: SubsubtopicItemDto[];
}

export class SubsubtopicItemDto {
  readonly id?: string;
  readonly name!: string;
}

/** A line of the "Indicadores" block of the daily report: "El Tiempo Divisas", optionally a link. */
export class ReportIndicatorDto {
  readonly source!: string;
  readonly label!: string;
  readonly url?: string;
}

export class ClientDto {
  readonly id?: string;
  readonly name!: string;
  readonly words?: WordDto[];
  readonly topics?: TopicItemDto[];
  /** mediaName → array of user phone numbers */
  readonly alerts?: Record<string, string[]>;
  /** mediaName → email addresses that also receive each alert by email */
  readonly alertEmails?: Record<string, string[]>;
  /** mediaName → array of user phone numbers */
  readonly notes?: Record<string, string[]>;
  /** Logo heading every page of the daily report: a PNG or JPEG data URL (500 KB at most). */
  readonly reportLogo?: string;
  /** The "Indicadores" block of the daily report. */
  readonly reportIndicators?: ReportIndicatorDto[];
  /** Google Alerts RSS feeds (https://www.google.com/alerts/feeds/...) the worker reads for this client's notes. */
  readonly googleAlertsFeeds?: string[];
}

export class MediaTypeDto {
  readonly id?: string;
  /** Internal key: internet | radio | tv | prensa | redes */
  readonly name!: string;
  /** Display label shown in the UI */
  readonly label!: string;
}
