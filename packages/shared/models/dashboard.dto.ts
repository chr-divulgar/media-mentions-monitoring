/** DTO para la respuesta del endpoint de dashboard con datos pre-calculados */

import { NoteSentiment } from "./note.enum";

export type DashboardPeriod = "semana" | "mes" | "trimestre" | "anual";

export interface ChartDataItem {
  type: string;
  value: number;
}

export interface TableDataItem {
  topic: string;
  subtopic: string;
  audience: number;
  totalNotes: number;
  [NoteSentiment.POSITIVO]: string;
  [NoteSentiment.NEGATIVO]: string;
  [NoteSentiment.NEUTRO]: string;
}

/** Datos pre-calculados para la sección "Comportamiento y temáticas principales" */
export interface DashboardBehaviorSection {
  totalNotes: number;
  directNotes: number;
  indirectNotes: number;
  tableData: TableDataItem[];
  sentimentData: ChartDataItem[];
  /** Porcentaje de diferencia en publicaciones directas vs período anterior */
  comparisonDirectPercentage?: number;
}

export interface TableWithPeriod {
  startDate: string;
  endDate: string;
  tableData: TableDataItem[];
}
/** Datos pre-calculados para la sección "Publicaciones y audiencia por sentimiento" */
export interface DashboardSentimentSection {
  subTopicTop5: TableDataItem[];
  tableByTopic: TableDataItem[];
}

export interface DashboardPerformanceSection {
  resultsByPeriod: TableWithPeriod[];
  tablesByPeriod: TableWithPeriod[];
  tablesPeriod?: TableWithPeriod[];
}

/** Un ítem de sentimientos por nombre de medio dentro de un grupo de tipo de medio */
export interface MediaNameSentimentItem {
  mediaName: string;
  [NoteSentiment.NEGATIVO]: number;
  [NoteSentiment.NEUTRO]: number;
  [NoteSentiment.POSITIVO]: number;
  totalNotes: number;
}

/** Agrupación por tipo de medio con sus ítems y audiencia total */
export interface MediaGroupItem {
  media: string;
  items: MediaNameSentimentItem[];
  totalAudience: number;
}

/** Un ítem del top 20 por nombre de medio */
export interface TableByMediaNameItem {
  mediaName: string;
  [NoteSentiment.NEGATIVO]: number;
  [NoteSentiment.NEUTRO]: number;
  [NoteSentiment.POSITIVO]: number;
  totalNotes: number;
  audience: number;
  /** true si el campo zone del medio es 'nacional' */
  isNational: boolean;
}

/** @deprecated usar TableByMediaNameItem */
export type TableByMediaName = TableByMediaNameItem;

export interface DashboardPresidentImpactItem {
  title: string;
  mediaNames: string[];
  repeatCount: number;
  sentiment: NoteSentiment;
}

export interface DashboardPresidentSection {
  tableByMediaName: TableByMediaNameItem[];
  totalNotes: number;
  totalAudience: number;
  positiveNotes: number;
  neutralNotes: number;
  negativeNotes: number;
  topImpact: DashboardPresidentImpactItem | null;
}

/** Respuesta completa del endpoint /notes/dashboard */
export interface DashboardDataDto {
  period: DashboardPeriod;
  behavior: DashboardBehaviorSection;
  sentiment: DashboardSentimentSection;
  performance: DashboardPerformanceSection;
  tableByMediaName: TableByMediaNameItem[];
  tableByMedia: MediaGroupItem[];
  president: DashboardPresidentSection;
}
