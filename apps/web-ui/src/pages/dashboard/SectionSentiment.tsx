import React from "react";
import { DualAxes } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
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
  const barData = tableByTopic.flatMap((row) => [
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
  ]);

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
  const sentimentColors = [
    NoteSentimentColor.NEGATIVO,
    NoteSentimentColor.NEUTRO,
    NoteSentimentColor.POSITIVO,
  ];

  const dualConfig = {
    height: 250,
    children: [
      {
        type: "area",
        data: areaData,
        encode: { x: "topic", y: "audience" },
        scale: { y: { key: "right" } },
        style: {
          fill: "#1890ff",
          fillOpacity: 0.1,
          stroke: "transparent",
          lineWidth: 0,
        },
        axis: {
          y: { title: "Audiencia", position: "right" },
        },
        labels: [
          {
            text: "audience",
            fill: "#1890ff",
            fontSize: 11,
          },
        ],
        legend: false,
        tooltip: (datum: { topic: string; audience: number }) => ({
          name: "Audiencia",
          value: datum.audience,
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
          {
            text: "value",
            position: "inside",
            fill: "#fff",
            fontSize: 11,
          },
        ],
        axis: {
          y: { title: "Menciones", position: "left" },
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
  const sentimentColorMap: Record<string, string> = {
    [NoteSentiment.NEGATIVO]: "#ff4d4f",
    [NoteSentiment.NEUTRO]: "#8c8c8c",
    [NoteSentiment.POSITIVO]: "#52c41a",
  };

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
            flex: 1,
            display: "flex",
            alignItems: "flex-start",
            justifyContent: "flex-start",
          }}
        >
          <div
            style={{
              ...DASHBOARD_THEME.titleStyle,
              fontSize: 16,
              textAlign: "center",
            }}
          >
            Temas con mayores publicaciones por sentimiento y audiencia
          </div>
        </div>

        {/* Columna 2-3 (2/5): Tabla subTopicTop5 */}
        <div
          style={{
            flex: 2,
            overflow: "auto",
          }}
        >
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
                >
                  Tema
                </th>
                <th
                  style={{
                    textAlign: "right",
                    borderBottom: "1px solid #e8e8e8",
                  }}
                >
                  #
                </th>
                <th
                  style={{
                    textAlign: "right",
                    borderBottom: "1px solid #e8e8e8",
                  }}
                >
                  Audiencia
                </th>
                {sentimentColumnsTable.map((column) => (
                  <th
                    key={column}
                    style={{
                      textAlign: "right",
                      borderBottom: "1px solid #e8e8e8",
                      textTransform: "capitalize",
                      color: sentimentColorMap[column] ?? "inherit",
                    }}
                  >
                    {column}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {subTopicTop5.map((row, index) => (
                <tr key={`${row.topic}-${row.subtopic}-${index}`}>
                  <td
                    style={{
                      borderBottom: "1px solid #f0f0f0",
                    }}
                  >
                    {row.subtopic || row.topic}
                  </td>
                  <td
                    style={{
                      textAlign: "right",
                      borderBottom: "1px solid #f0f0f0",
                    }}
                  >
                    {row.totalNotes}
                  </td>
                  <td
                    style={{
                      textAlign: "right",
                      borderBottom: "1px solid #f0f0f0",
                    }}
                  >
                    {row.audience}
                  </td>
                  {sentimentColumnsTable.map((column) => (
                    <td
                      key={`${row.subtopic}-${column}-${index}`}
                      style={{
                        textAlign: "center",
                        borderBottom: "1px solid #f0f0f0",
                        color: sentimentColorMap[column] ?? "inherit",
                      }}
                    >
                      {Number(row[column] ?? 0) > 0 ? row[column] : ""}
                    </td>
                  ))}
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
            flex: 1,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "flex-start",
            textAlign: "center",
          }}
        >
          <div
            style={{
              color: "#3C357B",
              fontSize: 14,
              lineHeight: 1.2,
            }}
          >
            Total publicaciones
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
            {totalPublicaciones}
          </div>
          <div
            style={{
              color: "#3C357B",
              fontSize: 14,
              lineHeight: 1.2,
              marginTop: 16,
            }}
          >
            Potencial audiencia alcanzada
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
            {totalAudiencia}
          </div>
        </div>

        {/* Columna 5 (1/5): Vacía */}
        <div style={{ flex: 1 }}></div>
      </div>
    </div>
  );
};

export default SectionSentiment;
