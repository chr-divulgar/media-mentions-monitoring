import React from "react";
import { Column } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionTop20ByMediaNameProps } from "./types";

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
      word ? `${word.charAt(0).toLocaleUpperCase("es-CO")}${word.slice(1)}` : word,
    )
    .join(" ");

const SectionTop20ByMediaName: React.FC<SectionTop20ByMediaNameProps> = ({
  dateRange,
  tableDataByMediaName,
}) => {
  const top20ByMediaName = tableDataByMediaName.slice(0, 20);
  const top20TotalNotes = top20ByMediaName.reduce(
    (acc, item) => acc + item.totalNotes,
    0,
  );
  const totalNotesAllMedia = tableDataByMediaName.reduce(
    (acc, item) => acc + item.totalNotes,
    0,
  );
  const totalMediaCount = tableDataByMediaName.length;
  const topMediaCount = top20ByMediaName.length;

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
    height: 300,
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
        Top 20 que más publicaron por sentimiento
      </div>

      <div
        style={{
          height: 300,
          minHeight: 300,
          maxHeight: 300,
          flex: "0 0 300px",
        }}
      >
        <Column {...chartConfig} />
      </div>

      <div
        style={{
          display: "flex",
          width: "100%",
          alignItems: "flex-start",
        }}
      >
        <div
          style={{
            width: "50%",
            color: DASHBOARD_THEME.titleColor,
            fontSize: 26,
            fontWeight: 600,
            lineHeight: 1.25,
            textAlign: "center",
          }}
        >
          En total {topMediaCount} medios publicaron
          {top20TotalNotes.toLocaleString("es-CO")} noticias directas sobre
          Ecopetrol o GE.
        </div>
        <div
          style={{
            width: "25%",
            fontSize: 16,
            lineHeight: 1.35,
            color: "#262626",
            textAlign: "center",
          }}
        >
          Total registros {totalNotesAllMedia.toLocaleString("es-CO")} en
          {totalMediaCount.toLocaleString("es-CO")} medios.
        </div>

        <div style={{ width: "25%" }} />
      </div>
    </div>
  );
};

export default SectionTop20ByMediaName;
