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
  behaviorData: {
    sentimentData,
    totalNotes,
    directNotes,
    indirectNotes,
    tableData,
  },
}) => {
  const directPct =
    totalNotes > 0 ? ((directNotes / totalNotes) * 100).toFixed(1) : "0.0";
  const indirectPct =
    totalNotes > 0 ? ((indirectNotes / totalNotes) * 100).toFixed(1) : "0.0";
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
    (column) => column !== totalNotesColumn,
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
              fontSize: 14,
              fontWeight: 500,
              marginTop: 4,
            }}
          >
            #
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
            flex: 4,
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
                fontSize: 10,
                color: "#4d4d4d",
              }}
            >
              <thead>
                <tr style={{ background: "#f5f5f5" }}>
                  <th
                    style={{
                      textAlign: "left",
                      borderBottom: "1px solid #e8e8e8",
                    }}
                  ></th>
                  {totalNotesColumn && (
                    <th
                      style={{
                        textAlign: "right",
                        borderBottom: "1px solid #e8e8e8",
                      }}
                    >
                      #
                    </th>
                  )}
                  <th
                    style={{
                      textAlign: "right",
                      borderBottom: "1px solid #e8e8e8",
                    }}
                  >
                    Audiencia
                  </th>
                  {sentimentColumnsWithoutTotalNotes.map((column) => (
                    <th
                      key={column}
                      style={{
                        textAlign: "right",
                        borderBottom: "1px solid #e8e8e8",
                        textTransform: "capitalize",
                        color: getSentimentTextColor(column) ?? "inherit",
                      }}
                    >
                      {column}
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
                              textAlign: "right",
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
                          {row.audience}
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
            flex: 3,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
            textAlign: "center",
          }}
        >
          <div
            style={{
              fontSize: 14,
              fontWeight: 500,
              marginBottom: 8,
              color: "#333333",
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
              color: "#3C357B",
              fontSize: 20,
              marginTop: 8,
              lineHeight: 1.2,
            }}
          >
            Potencial audiencia publicaciones directas
          </div>
          <div
            style={{
              color: "#000",
              fontSize: 24,
              fontWeight: 700,
              lineHeight: 1.1,
              marginTop: 4,
            }}
          >
            {totalAudience}
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
