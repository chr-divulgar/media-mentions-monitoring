/** Tipos compartidos del dashboard */

// Re-exportar el tipo de item de gráfico desde shared
import {
  DashboardBehaviorSection,
  DashboardPerformanceSection,
  DashboardPresidentSection,
  DashboardSentimentSection,
  DashboardZoneSection,
  DashboardPeriod,
  TableByMediaNameItem,
  MediaGroupItem,
} from "@repo/shared";

/** Props base que reciben todas las secciones del dashboard */
export interface DashboardSectionProps {
  /** Texto del rango de fechas formateado, e.g. "Abril 01 a 07 de 2026" */
  dateRange: string;
  /** Período seleccionado */
  period: DashboardPeriod;
}

/** Props de la sección "Comportamiento y temáticas principales" */
export interface SectionBehaviorProps extends DashboardSectionProps {
  behaviorData: DashboardBehaviorSection;
}

/** Props de la sección "Publicaciones y audiencia por sentimiento" */
export interface SectionSentimentProps extends DashboardSectionProps {
  sentimentData: DashboardSentimentSection;
}

/** Props de la sección "Desempeño por sentimiento" */
export interface SectionPerformanceProps extends DashboardSectionProps {
  performanceData: DashboardPerformanceSection;
}

/** Props de la sección "Top 20 que más publicaron por sentimiento" */
export interface SectionTop20ByMediaNameProps extends DashboardSectionProps {
  tableDataByMediaName: TableByMediaNameItem[];
}

/** Props de la sección "Publicaciones sobre el Presidente" */
export interface SectionPresidentProps extends DashboardSectionProps {
  presidentData: DashboardPresidentSection;
}

/** Props de la sección "Participación por tipo de medio" */
export interface SectionByMediaProps extends DashboardSectionProps {
  tableByMedia: MediaGroupItem[];
}

/** Props de la sección "Publicaciones de notas por regiones" */
export interface SectionByZoneProps extends DashboardSectionProps {
  sectionByZone: DashboardZoneSection;
}
