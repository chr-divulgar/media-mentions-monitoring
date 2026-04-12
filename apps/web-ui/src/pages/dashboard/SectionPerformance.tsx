import React from "react";
import dayjs from "dayjs";
import { Column } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionPerformanceProps } from "./types";

const meses = [
  "enero",
  "febrero",
  "marzo",
  "abril",
  "mayo",
  "junio",
  "julio",
  "agosto",
  "septiembre",
  "octubre",
  "noviembre",
  "diciembre",
];

const formatDateRange = (startDate: string, endDate: string): string => {
  const inicio = dayjs(startDate, "YYYY-MM-DD");
  const fin = dayjs(endDate, "YYYY-MM-DD");

  const diaInicio = inicio.format("DD");
  const mesInicio = meses[inicio.month()];
  const diaFin = fin.format("DD");
  const mesFin = meses[fin.month()];
  const anio = fin.format("YYYY");

  if (mesInicio === mesFin) {
    return `${mesInicio.charAt(0).toUpperCase() + mesInicio.slice(1)} ${diaInicio} a ${diaFin} de ${anio}`;
  }

  return `${mesInicio.charAt(0).toUpperCase() + mesInicio.slice(1)} ${diaInicio} a ${mesFin.charAt(0).toUpperCase() + mesFin.slice(1)} ${diaFin} de ${anio}`;
};

const SectionPerformance: React.FC<SectionPerformanceProps> = ({
  dateRange,
  performanceData,
}) => {
  const { resultsByPeriod } = performanceData;
  const tablesByPeriod =
    performanceData.tablesByPeriod ?? performanceData.tablesPeriod ?? [];

  const barData = resultsByPeriod.flatMap((periodItem) => {
    const row = Array.isArray(periodItem.tableData)
      ? periodItem.tableData[0]
      : periodItem.tableData;

    if (!row) {
      return [];
    }

    const rangeLabel = formatDateRange(
      periodItem.startDate,
      periodItem.endDate,
    );

    return [
      {
        range: rangeLabel,
        sentiment: NoteSentiment.NEGATIVO,
        value: Number(row[NoteSentiment.NEGATIVO] ?? 0),
      },
      {
        range: rangeLabel,
        sentiment: NoteSentiment.NEUTRO,
        value: Number(row[NoteSentiment.NEUTRO] ?? 0),
      },
      {
        range: rangeLabel,
        sentiment: NoteSentiment.POSITIVO,
        value: Number(row[NoteSentiment.POSITIVO] ?? 0),
      },
    ];
  });

  const sentimentColorMap: Record<string, string> = {
    [NoteSentiment.NEGATIVO]: NoteSentimentColor.NEGATIVO,
    [NoteSentiment.NEUTRO]: NoteSentimentColor.NEUTRO,
    [NoteSentiment.POSITIVO]: NoteSentimentColor.POSITIVO,
  };

  const sentimentOrder = [
    NoteSentiment.NEGATIVO,
    NoteSentiment.NEUTRO,
    NoteSentiment.POSITIVO,
  ];

  const sentimentColors = sentimentOrder.map(
    (sentiment) => sentimentColorMap[sentiment],
  );

  const chartConfig = {
    data: barData,
    xField: "range",
    yField: "value",
    seriesField: "sentiment",
    isGroup: true,
    colorField: "sentiment",
    color: sentimentColors,
    scale: {
      color: {
        domain: sentimentOrder,
        range: sentimentColors,
      },
    },
    legend: { position: "top" as const },
    xAxis: {
      label: {
        autoRotate: true,
        style: { fontSize: 10 },
      },
    },
    yAxis: {
      title: { text: "Número de notas" },
    },
    style: {
      maxWidth: 30,
    },
    label: {
      position: "top" as const,
      style: { fontSize: 10 },
    },
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
      {dateRange && <div style={DASHBOARD_THEME.dateStyle}>{dateRange}</div>}

      <div style={DASHBOARD_THEME.titleStyle}>Desempeño por sentimiento</div>

      <div style={{ height: 150, marginTop: 8 }}>
        <Column {...chartConfig} />
      </div>
      <div
        style={{
          flex: 1,
          display: "flex",
          overflow: "hidden",
        }}
      >
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "1fr 1fr",
            flex: 4,
            overflow: "hidden",
          }}
        >
          {tablesByPeriod.map((periodTable, index) => {
            const rangeLabel = formatDateRange(
              periodTable.startDate,
              periodTable.endDate,
            );
            const tableRows = periodTable.tableData.slice(0, 5);

            return (
              <div
                key={`${periodTable.startDate}-${periodTable.endDate}-${index}`}
                style={{
                  border: "1px solid #f0f0f0",
                  borderRadius: 4,
                  padding: 6,
                  overflow: "hidden",
                }}
              >
                <div
                  style={{
                    ...DASHBOARD_THEME.dateStyle,
                    marginBottom: 4,
                    padding: 0,
                    fontSize: 11,
                  }}
                >
                  {rangeLabel}
                </div>

                <table
                  style={{
                    width: "100%",
                    borderCollapse: "collapse",
                    fontSize: 9,
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
                      <th
                        style={{
                          textAlign: "center",
                          borderBottom: "1px solid #e8e8e8",
                          color: NoteSentimentColor.NEGATIVO,
                        }}
                      >
                        {NoteSentiment.NEGATIVO}
                      </th>
                      <th
                        style={{
                          textAlign: "center",
                          borderBottom: "1px solid #e8e8e8",
                          color: NoteSentimentColor.NEUTRO,
                        }}
                      >
                        {NoteSentiment.NEUTRO}
                      </th>
                      <th
                        style={{
                          textAlign: "center",
                          borderBottom: "1px solid #e8e8e8",
                          color: NoteSentimentColor.POSITIVO,
                        }}
                      >
                        {NoteSentiment.POSITIVO}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {tableRows.map((row, rowIndex) => (
                      <tr key={`${row.topic}-${row.subtopic}-${rowIndex}`}>
                        <td style={{ borderBottom: "1px solid #f0f0f0" }}>
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
                        <td
                          style={{
                            textAlign: "center",
                            borderBottom: "1px solid #f0f0f0",
                            color: NoteSentimentColor.NEGATIVO,
                          }}
                        >
                          {Number(row[NoteSentiment.NEGATIVO] ?? 0) > 0
                            ? row[NoteSentiment.NEGATIVO]
                            : ""}
                        </td>
                        <td
                          style={{
                            textAlign: "center",
                            borderBottom: "1px solid #f0f0f0",
                            color: NoteSentimentColor.NEUTRO,
                          }}
                        >
                          {Number(row[NoteSentiment.NEUTRO] ?? 0) > 0
                            ? row[NoteSentiment.NEUTRO]
                            : ""}
                        </td>
                        <td
                          style={{
                            textAlign: "center",
                            borderBottom: "1px solid #f0f0f0",
                            color: NoteSentimentColor.POSITIVO,
                          }}
                        >
                          {Number(row[NoteSentiment.POSITIVO] ?? 0) > 0
                            ? row[NoteSentiment.POSITIVO]
                            : ""}
                        </td>
                      </tr>
                    ))}

                    {tableRows.length === 0 && (
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
            );
          })}
        </div>
        <div style={{ flex: 1 }}></div>
      </div>
    </div>
  );
};

export default SectionPerformance;
