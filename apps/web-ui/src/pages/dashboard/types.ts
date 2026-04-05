/** Tipos compartidos del dashboard */

// Re-exportar el tipo de item de gráfico desde shared
import {
  DashboardBehaviorSection,
  DashboardSentimentSection,
} from "@repo/shared";

/** Props base que reciben todas las secciones del dashboard */
export interface DashboardSectionProps {
  /** Texto del rango de fechas formateado, e.g. "Abril 01 a 07 de 2026" */
  dateRange: string;
}

/** Props de la sección "Comportamiento y temáticas principales" */
export interface SectionBehaviorProps extends DashboardSectionProps {
  behaviorData: DashboardBehaviorSection;
}

/** Props de la sección "Publicaciones y audiencia por sentimiento" */
export interface SectionSentimentProps
  extends DashboardSectionProps, DashboardSentimentSection {}
