import React from "react";
import { DualAxes } from "@ant-design/plots";
import { NoteSentiment } from "@repo/shared";
import {
  DASHBOARD_THEME,
  getSentimentColor,
  getSentimentTextColor,
} from "./DashboardTheme";
import type { SectionSentimentProps } from "./types";

/**
 * Sección 2: "Publicaciones y audiencia por sentimiento"
 * Gráfico de barras apiladas (menciones por sentimiento) + área (audiencia), doble eje Y.
 */
const SectionSentiment: React.FC<SectionSentimentProps> = ({
  dateRange: fechaRango,
  sentimentData: { tableByTopic, subTopicTop5 },
}) => {
  // Buscar la fila "Total" en tableByTopic
  const totalRow = tableByTopic.find(
    (row) => row.topic.toLowerCase() === "total",
  );
  const totalPublicaciones = totalRow?.totalNotes ?? 0;
  const totalAudiencia = totalRow?.audience ?? 0;

  // Datos para las barras: una fila por (topic × sentimiento), orden fijo: Negativa → Neutra → Positiva
  // Precalcular totales por topic para mostrar arriba de la barra
  const topicTotals: Record<string, number> = {};
  const barData = tableByTopic.flatMap((row) => {
    const total =
      Number(row[NoteSentiment.NEGATIVO]) +
      Number(row[NoteSentiment.NEUTRO]) +
      Number(row[NoteSentiment.POSITIVO]);
    topicTotals[row.topic] = total;
    return [
      {
        topic: row.topic,
        sentiment: NoteSentiment.NEGATIVO,
        value: Number(row[NoteSentiment.NEGATIVO]),
      },
      {
        topic: row.topic,
        sentiment: NoteSentiment.NEUTRO,
        value: Number(row[NoteSentiment.NEUTRO]),
      },
      {
        topic: row.topic,
        sentiment: NoteSentiment.POSITIVO,
        value: Number(row[NoteSentiment.POSITIVO]),
      },
    ];
  });

  // Datos para el área: una fila por topic
  const areaData = tableByTopic.map((row) => ({
    topic: row.topic,
    audience: row.audience,
  }));

  const sentimentOrder = [
    NoteSentiment.NEGATIVO,
    NoteSentiment.NEUTRO,
    NoteSentiment.POSITIVO,
  ];
  const sentimentColors = sentimentOrder.map(getSentimentColor);

  const dualConfig = {
    height: 250,
    children: [
      {
        type: "area",
        data: areaData,
        encode: { x: "topic", y: "audience" },
        scale: { y: { key: "right" } },
        style: {
          fill: "#bfbfbf",
          stroke: "transparent",
          lineWidth: 0,
        },
        axis: {
          y: { position: "right" },
        },
        labels: [
          {
            text: (datum: { topic: string; audience: number }) => {
              return Number(datum.audience).toLocaleString("es-CO");
            },

            fontSize: 12,
            dx: 20,
            textAlign: "left",
            textBaseline: "bottom",
          },
        ],
        legend: { color: { title: false } },
        tooltip: (datum: { topic: string; audience: number }) => ({
          name: "Audiencia",
          value: Number(datum.audience).toLocaleString("es-CO"),
        }),
      },
      {
        type: "interval" as const,
        data: barData,
        encode: { x: "topic", y: "value", color: "sentiment" },
        transform: [{ type: "stackY" }],
        scale: {
          color: {
            domain: sentimentOrder,
            range: sentimentColors,
          },
        },
        style: {
          maxWidth: 40,
        },
        labels: [
          // Valor individual: dentro si es grande, fuera a la derecha con línea si es pequeño
          {
            text: (datum: { value: number }) =>
              datum.value > 0 ? datum.value.toLocaleString("es-CO") : "",
            position: (datum: { value: number }) =>
              datum.value >= 10 ? "inside" : "right",
            fill: (datum: { value: number }) =>
              datum.value >= 10 ? "#fff" : "#3C357B",
            fontSize: 11,
            fontWeight: (datum: { value: number }) =>
              datum.value >= 10 ? 400 : 700,
            labelLine: (datum: { value: number }) => datum.value < 10,
            dx: (datum: { value: number }) => (datum.value >= 10 ? 0 : 8),
            dy: (datum: { value: number; sentiment: NoteSentiment }) => {
              if (datum.value >= 10) return 0;
              // Separar verticalmente según el sentimiento
              switch (datum.sentiment) {
                case NoteSentiment.NEGATIVO:
                  return -12;
                case NoteSentiment.NEUTRO:
                  return 0;
                case NoteSentiment.POSITIVO:
                  return 12;
                default:
                  return 0;
              }
            },
          },
          // Total arriba de la barra
          {
            text: (datum: {
              topic: string;
              sentiment: NoteSentiment;
              value: number;
            }) => {
              if (datum.sentiment === NoteSentiment.POSITIVO) {
                const total = topicTotals[datum.topic];
                return total > 0 ? total.toLocaleString("es-CO") : "";
              }
              return "";
            },
            position: "top",
            fill: "#3C357B",
            fontSize: 12,
            fontWeight: 700,
            dy: -14,
          },
        ],
        axis: {
          y: { position: "left" },
        },
        legend: { color: { title: false } },
      },
    ],
  };

  // Columnas de sentimiento para la tabla
  const sentimentColumnsTable = [
    NoteSentiment.NEGATIVO,
    NoteSentiment.NEUTRO,
    NoteSentiment.POSITIVO,
  ];

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
      {fechaRango && <div style={DASHBOARD_THEME.dateStyle}>{fechaRango}</div>}
      {/* Título */}
      <div style={DASHBOARD_THEME.titleStyle}>
        {"Publicaciones y audiencia por sentimiento"}
      </div>
      {/* Gráfica */}
      <div style={{ height: "250px" }}>
        <DualAxes {...dualConfig} autoFit />
      </div>

      {/* Sección inferior dividida en 5 partes */}
      <div
        style={{
          flex: 1,
          display: "flex",
          gap: 12,
          marginTop: 12,
          overflow: "hidden",
        }}
      >
        {/* Columna 1 (1/5): Subtítulo */}
        <div
          style={{
            flex: 3,
            display: "flex",
            alignItems: "flex-start",
            justifyContent: "flex-start",
          }}
        >
          <div
            style={{
              ...DASHBOARD_THEME.titleStyle,
              fontSize: 24,
              lineHeight: 1,
              fontFamily: DASHBOARD_THEME.bodyFontFamily,
            }}
          >
            Temas con mayores publicaciones y audiencia
          </div>
        </div>

        {/* Columna 2-3 (2/5): Tabla subTopicTop5 */}
        <div
          style={{
            flex: 6,
            overflow: "auto",
          }}
        >
          <table
            style={{
              width: "100%",
              borderCollapse: "collapse",
              color: "#000",
              lineHeight: 1,
              fontSize: 12,
              fontFamily: DASHBOARD_THEME.bodyFontFamily,
            }}
          >
            <thead>
              <tr style={{ background: DASHBOARD_THEME.headerBg }}>
                <th
                  style={{
                    textAlign: "left",
                    fontWeight: "normal",
                    border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                  }}
                >
                  Tema
                </th>
                <th
                  style={{
                    textAlign: "center",
                    fontWeight: "normal",
                    border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                  }}
                >
                  #
                </th>
                <th
                  style={{
                    textAlign: "right",
                    fontWeight: "normal",
                    border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                  }}
                >
                  Audiencia
                </th>
                {sentimentColumnsTable.map((column) => (
                  <th
                    key={column}
                    style={{
                      textAlign: "right",
                      border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      textTransform: "capitalize",
                      fontWeight: "normal",
                    }}
                  >
                    {column.slice(0, 3) + "."}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {subTopicTop5.map((row, index) => (
                <tr key={`${row.topic}-${row.subtopic}-${index}`}>
                  <td
                    style={{
                      border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                    }}
                  >
                    {row.subtopic || row.topic}
                  </td>
                  <td
                    style={{
                      textAlign: "center",
                      border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                    }}
                  >
                    {row.totalNotes}
                  </td>
                  <td
                    style={{
                      textAlign: "right",
                      border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      paddingLeft: 4,
                    }}
                  >
                    {Number(row.audience).toLocaleString("es-CO")}
                  </td>
                  {sentimentColumnsTable.map((column) => {
                    const bg = getSentimentColor(column);
                    return (
                      <td
                        key={`${row.subtopic}-${column}-${index}`}
                        style={{
                          textAlign: "center",
                          border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                          background: bg,
                          color: getSentimentTextColor(bg),
                        }}
                      >
                        {Number(row[column] ?? 0) > 0 ? row[column] : ""}
                      </td>
                    );
                  })}
                </tr>
              ))}
              {subTopicTop5.length === 0 && (
                <tr>
                  <td
                    colSpan={6}
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

        {/* Columna 4 (1/5): Totales */}
        <div
          style={{
            flex: 3,
            display: "flex",
            flexDirection: "column",
            alignItems: "flex-start",
            justifyContent: "flex-start",
            textAlign: "left",
            color: "#7f7f7f",
            fontFamily: DASHBOARD_THEME.bodyFontFamily,
          }}
        >
          <div
            style={{
              lineHeight: 1.2,
            }}
          >
            Total publicaciones
          </div>
          <div
            style={{
              fontWeight: "bold",
              lineHeight: 1.1,
              marginTop: 4,
              fontFamily: DASHBOARD_THEME.bodyFontFamily,
            }}
          >
            {totalPublicaciones}
          </div>
          <div
            style={{
              lineHeight: 1.2,
              marginTop: 16,
            }}
          >
            Potencial audiencia alcanzada
          </div>
          <div
            style={{
              fontWeight: "bold",
              lineHeight: 1.1,
              marginTop: 4,
              fontFamily: DASHBOARD_THEME.bodyFontFamily,
            }}
          >
            {Number(totalAudiencia).toLocaleString("es-CO")}
          </div>
        </div>
      </div>
      <div style={DASHBOARD_THEME.footerStyle}>
        {DASHBOARD_THEME.footerText}
      </div>
    </div>
  );
};

export default SectionSentiment;
