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
const FIXED_CHART_HEIGHT = 200;

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
          overflow: "visible",
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
            stack: true,
            colorField: "colorKey",
            style: { maxWidth: 16 },
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
                position: "inside" as const,
                fill: "#ffffff",
                fontSize: 8,
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
                overflow: "visible",
                position: "relative",
                zIndex: 1,
              }}
            >
              <div
                style={{
                  ...DASHBOARD_THEME.titleStyle,
                  fontSize: 24,
                  lineHeight: 1,
                }}
              >
                {group.media}
              </div>
              <div
                style={{
                  ...DASHBOARD_THEME.titleStyle,
                  fontSize: 24,
                  lineHeight: 1,
                }}
              >
                {group.items.reduce((sum, item) => sum + item.totalNotes, 0) +
                  " notas"}
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
                  ...DASHBOARD_THEME.titleStyle,
                  fontSize: 16,
                  fontWeight: "normal",
                }}
              >
                Potencial audiencia alcanzada:{" "}
                <span style={{ fontWeight: "bold" }}>
                  {group.totalAudience.toLocaleString("es-CO")}
                </span>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default SectionByMedia;
