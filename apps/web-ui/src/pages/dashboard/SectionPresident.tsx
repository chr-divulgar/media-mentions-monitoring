import React from "react";
import { Column } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionPresidentProps } from "./types";

const COLOR_TOTAL_NACIONAL = "#1890ff";
const COLOR_TOTAL_LOCAL = "#faad14";

const SERIES_TOTAL_NACIONAL = "Total Nacional";
const SERIES_TOTAL_LOCAL = "Total Local";

const colorDomain = [
  NoteSentiment.NEGATIVO,
  NoteSentiment.NEUTRO,
  NoteSentiment.POSITIVO,
  SERIES_TOTAL_NACIONAL,
  SERIES_TOTAL_LOCAL,
];

const colorRange = [
  NoteSentimentColor.NEGATIVO,
  NoteSentimentColor.NEUTRO,
  NoteSentimentColor.POSITIVO,
  COLOR_TOTAL_NACIONAL,
  COLOR_TOTAL_LOCAL,
];

const toTitleCase = (value: string) =>
  value
    .toLocaleLowerCase("es-CO")
    .split(/\s+/)
    .map((word) =>
      word
        ? `${word.charAt(0).toLocaleUpperCase("es-CO")}${word.slice(1)}`
        : word,
    )
    .join(" ");

const SectionPresident: React.FC<SectionPresidentProps> = ({
  dateRange,
  presidentData,
}) => {
  const top20ByMediaName = presidentData.tableByMediaName.slice(0, 20);

  const barData = top20ByMediaName.flatMap((item) => {
    const formattedMediaName = toTitleCase(item.mediaName);
    const totalKey = item.isNational
      ? SERIES_TOTAL_NACIONAL
      : SERIES_TOTAL_LOCAL;

    return [
      {
        mediaName: formattedMediaName,
        colorKey: NoteSentiment.NEGATIVO,
        value: item[NoteSentiment.NEGATIVO],
      },
      {
        mediaName: formattedMediaName,
        colorKey: NoteSentiment.NEUTRO,
        value: item[NoteSentiment.NEUTRO],
      },
      {
        mediaName: formattedMediaName,
        colorKey: NoteSentiment.POSITIVO,
        value: item[NoteSentiment.POSITIVO],
      },
      {
        mediaName: formattedMediaName,
        colorKey: totalKey,
        value: item.totalNotes,
      },
    ];
  });

  const chartConfig = {
    height: 240,
    data: barData,
    xField: "mediaName",
    yField: "value",
    seriesField: "colorKey",
    group: true,
    colorField: "colorKey",
    color: colorRange,
    scale: {
      color: {
        domain: colorDomain,
        range: colorRange,
      },
    },
    legend: { position: "top" as const },
    appendPadding: [24, 0, 42, 0] as [number, number, number, number],
    axis: {
      x: {
        labelFontSize: 12,
        labelTransform: "translate(0, 0) rotate(-45)",
        labelTextAlign: "right",
        labelTextBaseline: "middle",
      },
      y: {
        title: { text: "Número de notas" },
      },
    },
    labels: [
      {
        text: "value",
        position: "top",
        fill: "#262626",
        fontSize: 9,
        dy: -10,
      },
    ],
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

      <div style={DASHBOARD_THEME.titleStyle}>
        Publicaciones sobre el Presidente por número, audiencia y sentimiento
      </div>

      <div style={{ height: 240, minHeight: 240, maxHeight: 240 }}>
        <Column {...chartConfig} />
      </div>

      <div
        style={{
          display: "flex",
          flexDirection: "row",
          flexWrap: "wrap",
          gap: 12,
          alignItems: "stretch",
          flex: 1,
        }}
      >
        <div style={{ flex: 4 }}>
          <div
            style={{
              ...DASHBOARD_THEME.titleStyle,
              fontSize: 18,
            }}
          >
            Publicaciones de mayor impacto
          </div>
          <div style={{ color: "#7f7f7f" }}>
            {presidentData.topImpact?.sentiment ?? NoteSentiment.NEGATIVO}
          </div>
          <div
            style={{
              color: DASHBOARD_THEME.titleStyle.color,

              lineHeight: 1,
              marginBottom: 4,
              whiteSpace: "pre-line",
            }}
          >
            {presidentData.topImpact?.mediaNames.length
              ? toTitleCase(presidentData.topImpact.mediaNames.join(", "))
              : "Sin medios registrados"}
          </div>
          <div
            style={{
              lineHeight: 1.2,
              color: "#7f7f7f",
              marginTop: 8,
            }}
          >
            {presidentData.topImpact?.title ?? "Sin título repetido"}
          </div>
        </div>

        <div style={{ flex: 2 }}>
          <div style={{ color: "#7f7f7f", lineHeight: 1.2 }}>
            <div>
              Total publicaciones{" "}
              <span style={{ fontWeight: "bold" }}>
                {presidentData.totalNotes.toLocaleString("es-CO")}
              </span>
            </div>
            <div>
              Positivas{" "}
              <span style={{ fontWeight: "bold" }}>
                {presidentData.positiveNotes.toLocaleString("es-CO")}
              </span>
            </div>
            <div>
              Neutras{" "}
              <span style={{ fontWeight: "bold" }}>
                {presidentData.neutralNotes.toLocaleString("es-CO")}
              </span>
            </div>
            <div>
              Negativas{" "}
              <span style={{ fontWeight: "bold" }}>
                {presidentData.negativeNotes.toLocaleString("es-CO")}
              </span>
            </div>
          </div>
        </div>

        <div
          style={{
            flex: 2,
            justifyContent: "center",
            color: "#000",
            lineHeight: 1.2,
          }}
        >
          Potencial audiencia alcanzada
          <div
            style={{
              fontWeight: "bold",
            }}
          >
            {presidentData.totalAudience.toLocaleString("es-CO")}
          </div>
        </div>
      </div>
    </div>
  );
};

export default SectionPresident;
