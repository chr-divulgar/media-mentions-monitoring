import React from "react";
import { Pie } from "@ant-design/plots";
import { DASHBOARD_THEME, getPieConfig } from "./DashboardTheme";
import type { SectionSentimentProps } from "./types";

/**
 * Sección 2: "Publicaciones y audiencia por sentimiento"
 * Muestra gráfico pie de tipo de medio.
 */
const SectionSentiment: React.FC<SectionSentimentProps> = ({
  dateRange: fechaRango,
  mediaData,
}) => {
  return (
    <div style={DASHBOARD_THEME.sectionContainer}>
      {/* Renglón de fecha */}
      {fechaRango && <div style={DASHBOARD_THEME.dateStyle}>{fechaRango}</div>}
      {/* Título */}
      <div style={DASHBOARD_THEME.titleStyle}>
        {"Publicaciones y audiencia por\nsentimiento"}
      </div>
      {/* Gráfico pie de tipo de medio */}
      <Pie
        {...getPieConfig(mediaData)}
        style={{
          width: 640,
          height: 360,
          margin: "0 auto",
          background: DASHBOARD_THEME.sectionBg,
        }}
      />
    </div>
  );
};

export default SectionSentiment;
