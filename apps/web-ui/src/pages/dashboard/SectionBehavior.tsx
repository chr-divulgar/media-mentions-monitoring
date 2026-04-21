import React from "react";
import { Pie } from "@ant-design/plots";
import { CaretUpFilled, CaretDownFilled } from "@ant-design/icons";
import { DASHBOARD_THEME, getPieConfig } from "./DashboardTheme";
import type { SectionBehaviorProps } from "./types";

/**
 * Sección 1: "Comportamiento y temáticas principales"
 * Muestra barra de resumen (publicaciones) + gráfico pie de sentimiento.
 */
const SectionBehavior: React.FC<SectionBehaviorProps> = ({
  dateRange,
  period,
  behaviorData: {
    sentimentData,
    totalNotes,
    directNotes,
    indirectNotes,
    tableData,
    comparisonDirectPercentage,
  },
}) => {
  const directPct =
    totalNotes > 0 ? ((directNotes / totalNotes) * 100).toFixed(1) : "0.0";
  const indirectPct =
    totalNotes > 0 ? ((indirectNotes / totalNotes) * 100).toFixed(1) : "0.0";

  const getPeriodLabel = (p: typeof period): string => {
    const labels: Record<typeof period, string> = {
      semana: "semana",
      mes: "mes",
      trimestre: "trimestre",
      anual: "año",
    };
    return labels[p];
  };

  const getArrow = (comparison?: number): React.ReactNode => {
    if (comparison === undefined || comparison === 0) return null;
    return comparison > 0 ? <CaretUpFilled /> : <CaretDownFilled />;
  };

  const comparisonValue = comparisonDirectPercentage ?? 0;
  const comparisonText =
    comparisonValue === 0 ? "" : Math.abs(comparisonValue).toFixed(1) + "%";

  const sentimentColumns = Array.from(
    new Set(
      tableData.flatMap((row) =>
        Object.keys(row).filter(
          (key) => !["topic", "subtopic", "audience"].includes(key),
        ),
      ),
    ),
  );
  const totalNotesColumn = sentimentColumns.find(
    (column) => column.toLowerCase() === "totalnotes",
  );
  const sentimentColumnsWithoutTotalNotes = sentimentColumns.filter(
    (column) =>
      column !== totalNotesColumn && column.toLowerCase() !== "origin",
  );
  const totalAudience = tableData.reduce(
    (sum, row) => sum + Number(row.audience ?? 0),
    0,
  );
  const sentimentColorMap: Record<string, string> = {
    negativa: "#ff4d4f",
    neutra: "#8c8c8c",
    positiva: "#52c41a",
  };
  const getSentimentTextColor = (column: string) =>
    sentimentColorMap[column.toLowerCase()];

  return (
    <div
      style={{
        ...DASHBOARD_THEME.sectionContainer,
        display: "flex",
        flexDirection: "column",
        backgroundImage: "url('/base.png')",
        backgroundSize: "cover",
        backgroundPosition: "center",
      }}
    >
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
              fontWeight: "bold",
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
              flex: 2,
              display: "flex",
              flexDirection: "column",
              alignItems: "center",
              justifyContent: "center",
              textAlign: "center",
              padding: "8px 6px",
            }}
          >
            <div style={{ fontSize: 24, fontWeight: "bold" }}>
              {directNotes}
            </div>
            <div>El volumen total de la conversación</div>
            <div style={{ fontSize: 22, fontWeight: "bold" }}>{directPct}%</div>
          </div>

          {/* Parte 2 */}
          <div
            style={{
              flex: 2,
              display: "flex",
              flexDirection: "column",
              justifyContent: "center",
              alignItems: "center",
              textAlign: "left",
            }}
          >
            <div
              style={{
                fontSize: 24,
                fontWeight: "bold",
                alignSelf: "center",
                lineHeight: 1.2,
              }}
            >
              Publicaciones directas
            </div>
            <ul
              style={{
                lineHeight: 1.2,
                margin: 0,
                paddingLeft: 18,
                listStyleType: "disc",
              }}
            >
              <li>Empresa o Grupo Empresarial</li>
              <li>Financiera - especializadas</li>
              <li>Presidente – Ricardo Roa</li>
            </ul>
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
            {comparisonText && (
              <div
                style={{
                  fontSize: 24,
                  fontWeight: "bold",
                  lineHeight: 1,
                }}
              >
                {getArrow(comparisonValue)} {comparisonText}
              </div>
            )}
            <div style={{ fontSize: 14 }}>
              vs. {getPeriodLabel(period)} anterior
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
          <div style={{ marginTop: 6, lineHeight: 1.25 }}>
            Publicaciones Indirectas
          </div>
          <div style={{ fontSize: 24, fontWeight: "bold", lineHeight: 1 }}>
            {indirectPct}%
          </div>
        </div>
      </div>

      <div
        style={{
          display: "flex",
          gap: 12,
          marginTop: 12,
          alignItems: "stretch",
          flex: 1,
          overflow: "hidden",
        }}
      >
        <div
          style={{
            flex: 12,
            border: "none",
            borderRadius: 0,
            overflow: "visible",
            display: "flex",
            flexDirection: "column",
          }}
        >
          <div>
            <table
              style={{
                width: "100%",
                borderCollapse: "collapse",
                fontSize: 16,
                lineHeight: 1,
                color: "#4d4d4d",
              }}
            >
              <thead>
                <tr style={{ background: "#fff" }}>
                  <th></th>
                  {totalNotesColumn && (
                    <th
                      style={{
                        textAlign: "center",
                      }}
                    >
                      #
                    </th>
                  )}
                  <th style={{ fontWeight: "initial", textAlign: "right" }}>
                    Audiencia
                  </th>
                  {sentimentColumnsWithoutTotalNotes.map((column) => (
                    <th
                      key={column}
                      style={{
                        textAlign: "center",
                        textTransform: "capitalize",
                        fontWeight: "initial",
                        color: getSentimentTextColor(column) ?? "inherit",
                      }}
                    >
                      {column.slice(0, 3) + "."}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {(() => {
                  const groupedByTopic: { [key: string]: typeof tableData } =
                    {};
                  tableData.forEach((row) => {
                    if (!groupedByTopic[row.topic]) {
                      groupedByTopic[row.topic] = [];
                    }
                    groupedByTopic[row.topic].push(row);
                  });

                  const topicOrder = Array.from(
                    new Set(tableData.map((row) => row.topic)),
                  ).sort(
                    (a, b) =>
                      (groupedByTopic[a]?.length ?? 0) -
                      (groupedByTopic[b]?.length ?? 0),
                  );

                  return topicOrder.flatMap((topic) => [
                    <tr
                      key={`topic-header-${topic}`}
                      style={{
                        background: "#f0f0f0",
                        fontWeight: 600,
                      }}
                    >
                      <td
                        colSpan={2 + sentimentColumns.length}
                        style={{
                          borderBottom: "1px solid #e8e8e8",
                          textAlign: "left",
                        }}
                      >
                        {topic}
                      </td>
                    </tr>,
                    ...(groupedByTopic[topic] ?? []).map((row, index) => (
                      <tr key={`${row.topic}-${row.subtopic}-${index}`}>
                        <td
                          style={{
                            borderBottom: "1px solid #f0f0f0",
                          }}
                        >
                          {row.subtopic}
                        </td>
                        {totalNotesColumn && (
                          <td
                            style={{
                              textAlign: "center",
                              borderBottom: "1px solid #f0f0f0",
                            }}
                          >
                            {String(
                              row[totalNotesColumn as keyof typeof row] ?? 0,
                            )}
                          </td>
                        )}
                        <td
                          style={{
                            textAlign: "right",
                            borderBottom: "1px solid #f0f0f0",
                          }}
                        >
                          {row.audience !== undefined && row.audience !== null
                            ? Number(row.audience).toLocaleString("es-CO")
                            : ""}
                        </td>
                        {sentimentColumnsWithoutTotalNotes.map((column) => (
                          <td
                            key={`${row.subtopic}-${column}-${index}`}
                            style={{
                              textAlign: "center",
                              borderBottom: "1px solid #f0f0f0",
                              color: getSentimentTextColor(column) ?? "inherit",
                            }}
                          >
                            {Number(row[column as keyof typeof row] ?? 0) > 0
                              ? row[column as keyof typeof row]
                              : ""}
                          </td>
                        ))}
                      </tr>
                    )),
                  ]);
                })()}
                {tableData.length === 0 && (
                  <tr>
                    <td
                      colSpan={2 + sentimentColumns.length}
                      style={{
                        textAlign: "center",
                        color: "#4d4d4d",
                      }}
                    >
                      No hay datos para mostrar
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>

        <div
          style={{
            flex: 6,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
            textAlign: "center",
          }}
        >
          <div
            style={{
              marginBottom: 8,
              color: "#333333",
              textAlign: "left",
              width: "100%",
              marginLeft: 24,
            }}
          >
            Sentimiento publicaciones directas
          </div>
          <div style={{ width: "100%", height: 190, maxWidth: 320 }}>
            <Pie
              {...getPieConfig(sentimentData)}
              style={{ width: "100%", height: 190 }}
            />
          </div>
          <div
            style={{
              color: DASHBOARD_THEME.titleStyle.color,
              fontSize: 24,
              fontWeight: "bold",
              lineHeight: 1.2,
              textAlign: "left",
            }}
          >
            Potencial audiencia publicaciones directas
          </div>
          <div
            style={{
              color: "#000",
              fontSize: 24,
              fontWeight: "bold",
              lineHeight: 1.1,
              width: "100%",
              textAlign: "left",
            }}
          >
            {Number(totalAudience).toLocaleString("es-CO")}
          </div>
        </div>

        <div
          style={{
            flex: 1,
          }}
        ></div>
      </div>
    </div>
  );
};

export default SectionBehavior;
