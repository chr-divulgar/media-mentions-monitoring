import React from "react";
import { Pie } from "@ant-design/plots";
import { DASHBOARD_THEME, getPieConfig } from "./DashboardTheme";
import type { SectionBehaviorProps } from "./types";

/**
 * Sección 1: "Comportamiento y temáticas principales"
 * Muestra barra de resumen (publicaciones) + gráfico pie de sentimiento.
 */
const SectionBehavior: React.FC<SectionBehaviorProps> = ({
  dateRange,
  behaviorData: { sentimentData, totalNotes, directNotes, indirectNotes },
}) => {
  const directPct =
    totalNotes > 0 ? ((directNotes / totalNotes) * 100).toFixed(1) : "0.0";
  const indirectPct =
    totalNotes > 0 ? ((indirectNotes / totalNotes) * 100).toFixed(1) : "0.0";

  return (
    <div style={DASHBOARD_THEME.sectionContainer}>
      {/* Renglón de fecha */}
      {dateRange && <div style={DASHBOARD_THEME.dateStyle}>{dateRange}</div>}
      {/* Título */}
      <div style={DASHBOARD_THEME.titleStyle}>
        Comportamiento y temáticas principales
      </div>
      {/* Barra de resumen */}
      <div
        style={{
          display: "flex",
          height: 90,
          borderRadius: 6,
          overflow: "hidden",
          marginTop: 4,
        }}
      >
        {/* Publicaciones */}
        <div
          style={{
            flex: 1,
            background: "#3c357b",
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
          }}
        >
          <span
            style={{
              color: "#fff",
              fontSize: 36,
              fontWeight: 700,
              lineHeight: 1,
            }}
          >
            {totalNotes}
          </span>
          <span
            style={{
              color: "#fff",
              fontSize: 14,
              fontWeight: 500,
              marginTop: 4,
            }}
          >
            Publicaciones
          </span>
        </div>

        {/* Sección verde dividida en 3 */}
        <div
          style={{
            flex: 6,
            background: "#00b050",
            marginLeft: 4,
            marginRight: 4,
            display: "flex",
            color: "#fff",
          }}
        >
          {/* Parte 1 */}
          <div
            style={{
              flex: 1,
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              justifyContent: "center",
              textAlign: "center",
              padding: "8px 6px",
            }}
          >
            <div style={{ fontSize: 30, fontWeight: 700, lineHeight: 1 }}>
              {directNotes}
            </div>
            <div style={{ fontSize: 12, marginTop: 6 }}>
              El volumen total de la conversación
            </div>
            <div style={{ fontSize: 16, fontWeight: 700, marginTop: 4 }}>
              {directPct}%
            </div>
          </div>

          {/* Parte 2 */}
          <div
            style={{
              flex: 1,
              display: "flex",
              flexDirection: "column",
              justifyContent: "center",
              padding: "8px 10px",
              borderLeft: "1px solid rgba(255,255,255,0.35)",
              borderRight: "1px solid rgba(255,255,255,0.35)",
              textAlign: "left",
            }}
          >
            <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 6 }}>
              Publicaciones directas
            </div>
            <div style={{ fontSize: 12, lineHeight: 1.25 }}>
              Empresa o Grupo Empresarial
            </div>
            <div style={{ fontSize: 12, lineHeight: 1.25 }}>
              Financiera - especializadas
            </div>
            <div style={{ fontSize: 12, lineHeight: 1.25 }}>
              Presidente – Ricardo Roa
            </div>
          </div>

          {/* Parte 3 */}
          <div
            style={{
              flex: 1,
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              justifyContent: "center",
              textAlign: "center",
              padding: "8px 6px",
            }}
          >
            <div style={{ fontSize: 34, fontWeight: 900, lineHeight: 1 }}>
              ⬆ 12.5%
            </div>
            <div style={{ fontSize: 13, marginTop: 6 }}>
              vs. semana anterior
            </div>
          </div>
        </div>

        {/* Sección gris */}
        <div
          style={{
            flex: 1,
            background: "#595959",
            color: "#fff",
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
            textAlign: "center",
            padding: "8px 6px",
          }}
        >
          <div style={{ fontSize: 28, fontWeight: 700, lineHeight: 1 }}>
            {indirectPct}%
          </div>
          <div style={{ fontSize: 14, marginTop: 6, lineHeight: 1.25 }}>
            Publicaciones Indirectas
          </div>
        </div>
      </div>

      {/* Gráfico pie de sentimiento */}
      <Pie
        {...getPieConfig(sentimentData)}
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

export default SectionBehavior;
