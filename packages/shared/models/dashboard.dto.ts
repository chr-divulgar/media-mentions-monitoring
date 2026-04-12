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

export interface tableWithPeriod {
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
  resultsByPeriod: tableWithPeriod[];
  tablesByPeriod: tableWithPeriod[];
  tablesPeriod?: tableWithPeriod[];
}

/** Un ítem del top 20 por nombre de medio */
export interface TableByMediaName {
  mediaName: string;
  [NoteSentiment.NEGATIVO]: number;
  [NoteSentiment.NEUTRO]: number;
  [NoteSentiment.POSITIVO]: number;
  totalNotes: number;
  /** true si el campo zone del medio es 'nacional' */
  isNational: boolean;
}

/** Respuesta completa del endpoint /notes/dashboard */
export interface DashboardDataDto {
  period: DashboardPeriod;
  behavior: DashboardBehaviorSection;
  sentiment: DashboardSentimentSection;
  performance: DashboardPerformanceSection;
  tableByMediaName: TableByMediaName[];
}
