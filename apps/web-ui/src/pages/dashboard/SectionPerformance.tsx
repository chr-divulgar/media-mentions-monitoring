import React from "react";
import dayjs from "dayjs";
import { DualAxes } from "@ant-design/plots";
import { NoteSentiment } from "@repo/shared";
import {
  DASHBOARD_THEME,
  getSentimentColor,
  getSentimentTextColor,
} from "./DashboardTheme";
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
  const chartPeriods = React.useMemo(
    () => [...resultsByPeriod].reverse(),
    [resultsByPeriod],
  );
  const tablesByPeriod =
    performanceData.tablesByPeriod ?? performanceData.tablesPeriod ?? [];
  const displayedTables = [...tablesByPeriod].slice(0, 3).reverse();
  const gridColumns = 2;
  const gridRows = Math.max(1, Math.ceil(displayedTables.length / gridColumns));
  const ymax = Math.max(
    ...resultsByPeriod.map((p) => p.tableData?.[0]?.totalNotes ?? 0),
  );
  const barData = chartPeriods.flatMap((periodItem) => {
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

  const sentimentOrder = [
    NoteSentiment.NEGATIVO,
    NoteSentiment.NEUTRO,
    NoteSentiment.POSITIVO,
  ];

  const sentimentColors = sentimentOrder.map(getSentimentColor);
  const trendLineColor = DASHBOARD_THEME.SENTIMENT_COLORS.neutra;

  const dualConfig = {
    height: 150,
    children: [
      {
        type: "interval",
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
        legend: { color: { title: false } },
        axis: {
          y: { title: "Menciones", position: "left" },
        },
        style: {
          maxWidth: 30,
        },
        label: {
          position: "top" as const,
          style: { fontSize: 10, dy: -11 },
        },
      },
      {
        type: "line" as const,
        data: chartPeriods.map((periodItem) => ({
          range: formatDateRange(periodItem.startDate, periodItem.endDate),
          value: periodItem.tableData?.[0]?.totalNotes ?? 0,
        })),
        encode: { x: "range", y: "value" },
        style: {
          stroke: trendLineColor,
          lineWidth: 4,
        },
        axis: {
          y: { position: "right" },
        },
        labels: [
          {
            text: (datum: { value: number }) =>
              datum.value > 0 ? datum.value.toLocaleString("es-CO") : "",
            position: "top",
            fill: "#000",
            fontSize: 13,
            fontWeight: "bold",
            dy: -10,
          },
        ],
        legend: false,
        tooltip: (datum: { value: number }) => ({
          name: "Total general",
          value: datum.value.toLocaleString("es-CO"),
        }),
      },
    ],
    scale: {
      y: { min: 0, max: ymax },
    },
    legend: {
      color: {
        position: "top",
        itemName: {
          formatter: (name: string) => {
            if (name === "total") return "Total general";
            return name;
          },
        },
        itemMarker: (name: string) => {
          if (name === "total") {
            return {
              symbol: "line",
              style: { stroke: trendLineColor, lineWidth: 4 },
            };
          }
          return { symbol: "square", style: { fill: getSentimentColor(name) } };
        },
      },
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

      <div style={{ height: 130 }}>
        <DualAxes {...dualConfig} autoFit />
      </div>

      <div
        style={{
          display: "grid",
          gridTemplateColumns: "1fr 1fr",
          gridTemplateRows: `repeat(${gridRows}, auto)`,
          gridAutoFlow: "column",
        }}
      >
        {displayedTables.map((periodTable, index) => {
          const rangeLabel = formatDateRange(
            periodTable.startDate,
            periodTable.endDate,
          );
          const tableRows = periodTable.tableData.slice(0, 5);

          return (
            <div
              key={`${periodTable.startDate}-${periodTable.endDate}-${index}`}
              style={{
                borderRadius: 4,
                padding: 6,
                overflow: "hidden",
              }}
            >
              <div
                style={{
                  color: "#00323f",
                  fontWeight: "bold",
                  fontSize: 18,
                  fontFamily: DASHBOARD_THEME.headingFontFamily,
                }}
              >
                {rangeLabel}
              </div>

              <table
                style={{
                  width: "100%",
                  borderCollapse: "collapse",
                  color: "#000",
                  fontSize: 12,
                  lineHeight: 1,
                  fontFamily: DASHBOARD_THEME.bodyFontFamily,
                }}
              >
                <thead>
                  <tr style={{ background: DASHBOARD_THEME.headerBg }}>
                    <th
                      style={{
                        textAlign: "left",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      Tema
                    </th>
                    <th
                      style={{
                        textAlign: "center",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      #
                    </th>
                    <th
                      style={{
                        textAlign: "right",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      Audiencia
                    </th>
                    <th
                      style={{
                        textAlign: "center",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      {NoteSentiment.NEGATIVO.slice(0, 3) + "."}
                    </th>
                    <th
                      style={{
                        textAlign: "center",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      {NoteSentiment.NEUTRO.slice(0, 3) + "."}
                    </th>
                    <th
                      style={{
                        textAlign: "center",
                        border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                      }}
                    >
                      {NoteSentiment.POSITIVO.slice(0, 3) + "."}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {tableRows.map((row, rowIndex) => (
                    <tr key={`${row.topic}-${row.subtopic}-${rowIndex}`}>
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
                        }}
                      >
                        {Number(row.audience).toLocaleString("es-CO")}
                      </td>
                      <td
                        style={{
                          textAlign: "center",
                          border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                          background: getSentimentColor(NoteSentiment.NEGATIVO),
                          color: getSentimentTextColor(
                            getSentimentColor(NoteSentiment.NEGATIVO),
                          ),
                        }}
                      >
                        {Number(row[NoteSentiment.NEGATIVO] ?? 0) > 0
                          ? row[NoteSentiment.NEGATIVO]
                          : ""}
                      </td>
                      <td
                        style={{
                          textAlign: "center",
                          border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                          background: getSentimentColor(NoteSentiment.NEUTRO),
                          color: getSentimentTextColor(
                            getSentimentColor(NoteSentiment.NEUTRO),
                          ),
                        }}
                      >
                        {Number(row[NoteSentiment.NEUTRO] ?? 0) > 0
                          ? row[NoteSentiment.NEUTRO]
                          : ""}
                      </td>
                      <td
                        style={{
                          textAlign: "center",
                          border: `1px solid ${DASHBOARD_THEME.borderColor}`,
                          background: getSentimentColor(NoteSentiment.POSITIVO),
                          color: getSentimentTextColor(
                            getSentimentColor(NoteSentiment.POSITIVO),
                          ),
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
      <div style={DASHBOARD_THEME.footerStyle}>
        {DASHBOARD_THEME.footerText}
      </div>
    </div>
  );
};

export default SectionPerformance;
