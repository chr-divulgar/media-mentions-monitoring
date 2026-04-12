import React from "react";
import { Bar } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionByMediaProps } from "./types";

const colorDomain = [
  NoteSentiment.NEGATIVO,
  NoteSentiment.NEUTRO,
  NoteSentiment.POSITIVO,
];

const colorRange = [
  NoteSentimentColor.NEGATIVO,
  NoteSentimentColor.NEUTRO,
  NoteSentimentColor.POSITIVO,
];

const MAX_ITEMS_PER_MEDIA = 6;
const FIXED_CHART_HEIGHT = 220;

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

const SectionByMedia: React.FC<SectionByMediaProps> = ({
  dateRange,
  tableByMedia,
}) => {
  if (!tableByMedia || tableByMedia.length === 0) return null;

  const sectionWidth = Number(DASHBOARD_THEME.sectionContainer.width) || 960;
  const sectionPadding = 24 * 2;
  const chartGap = 16;
  const totalGaps = Math.max(tableByMedia.length - 1, 0) * chartGap;
  const chartWidth = Math.max(
    120,
    (sectionWidth - sectionPadding - totalGaps) / tableByMedia.length,
  );

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
        Participación por tipo de medio
      </div>

      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 16,
          flexWrap: "wrap",
          marginTop: 8,
          marginBottom: 8,
        }}
      >
        {colorDomain.map((label, index) => (
          <div
            key={label}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 8,
              color: "#262626",
              fontSize: 13,
              fontWeight: 500,
            }}
          >
            <span
              style={{
                width: 12,
                height: 12,
                borderRadius: 2,
                backgroundColor: colorRange[index],
                display: "inline-block",
              }}
            />
            <span>{label}</span>
          </div>
        ))}
      </div>

      <div
        style={{
          flex: 1,
          display: "flex",
          flexWrap: "wrap",
          gap: 16,
          marginTop: 8,
          overflow: "hidden",
          alignContent: "flex-start",
        }}
      >
        {tableByMedia.map((group) => {
          const topItems = group.items.slice(0, MAX_ITEMS_PER_MEDIA);

          const barData = topItems.flatMap((item) => [
            {
              mediaName: toTitleCase(item.mediaName),
              colorKey: NoteSentiment.NEGATIVO,
              value: item[NoteSentiment.NEGATIVO],
            },
            {
              mediaName: toTitleCase(item.mediaName),
              colorKey: NoteSentiment.NEUTRO,
              value: item[NoteSentiment.NEUTRO],
            },
            {
              mediaName: toTitleCase(item.mediaName),
              colorKey: NoteSentiment.POSITIVO,
              value: item[NoteSentiment.POSITIVO],
            },
          ]);

          const chartHeight = FIXED_CHART_HEIGHT;

          const chartConfig = {
            height: chartHeight,
            data: barData,
            xField: "mediaName",
            yField: "value",
            seriesField: "colorKey",
            group: true,
            colorField: "colorKey",
            scale: {
              color: {
                domain: colorDomain,
                range: colorRange,
              },
            },
            legend: false as const,
            axis: {
              x: {
                labelFontSize: 9,
              },
              y: {
                labelFontSize: 9,
              },
            },
            labels: [
              {
                text: (d: { value: number }) =>
                  d.value > 0 ? String(d.value) : "",
                position: "right" as const,
                fill: "#262626",
                fontSize: 8,
                dx: 4,
              },
            ],
          };

          return (
            <div
              key={group.media}
              style={{
                flex: `0 0 ${chartWidth}px`,
                width: chartWidth,
                maxWidth: chartWidth,
                minWidth: chartWidth,
                display: "flex",
                flexDirection: "column",
                overflow: "hidden",
              }}
            >
              <div
                style={{
                  fontSize: 13,
                  fontWeight: 700,
                  color: "#262626",
                  marginBottom: 2,
                  textTransform: "uppercase",
                  letterSpacing: 0.5,
                }}
              >
                {group.media}
              </div>

              <div
                style={{
                  width: "100%",
                }}
              >
                <Bar {...chartConfig} />
              </div>

              <div
                style={{
                  color: DASHBOARD_THEME.titleColor,
                  fontSize: 13,
                  fontWeight: 600,
                  marginTop: 4,
                }}
              >
                Potencial audiencia alcanzada:{" "}
                {group.totalAudience.toLocaleString("es-CO")}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default SectionByMedia;
