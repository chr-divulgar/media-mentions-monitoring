/** DTO para la respuesta del endpoint de dashboard con datos pre-calculados */

export interface ChartDataItem {
  type: string;
  value: number;
}

/** Datos pre-calculados para la sección "Comportamiento y temáticas principales" */
export interface DashboardBehaviorSection {
  totalNotes: number;
  sentimentData: ChartDataItem[];
}

/** Datos pre-calculados para la sección "Publicaciones y audiencia por sentimiento" */
export interface DashboardSentimentSection {
  mediaData: ChartDataItem[];
}

/** Respuesta completa del endpoint /notes/dashboard */
export interface DashboardDataDto {
  behavior: DashboardBehaviorSection;
  sentiment: DashboardSentimentSection;
}
