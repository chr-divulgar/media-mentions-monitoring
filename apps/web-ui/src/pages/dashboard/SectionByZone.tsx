import React from "react";
import { DualAxes } from "@ant-design/plots";
import { NoteSentiment, NoteSentimentColor } from "@repo/shared";
import { DASHBOARD_THEME } from "./DashboardTheme";
import type { SectionByZoneProps } from "./types";

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

const SectionByZone: React.FC<SectionByZoneProps> = ({
  dateRange,
  sectionByZone: { tableByZone },
}) => {
  const barData = tableByZone.flatMap((row) => [
    {
      zone: toTitleCase(row.zone),
      sentiment: NoteSentiment.NEGATIVO,
      value: Number(row[NoteSentiment.NEGATIVO]),
    },
    {
      zone: toTitleCase(row.zone),
      sentiment: NoteSentiment.NEUTRO,
      value: Number(row[NoteSentiment.NEUTRO]),
    },
    {
      zone: toTitleCase(row.zone),
      sentiment: NoteSentiment.POSITIVO,
      value: Number(row[NoteSentiment.POSITIVO]),
    },
  ]);

  const areaData = tableByZone.map((row) => ({
    zone: toTitleCase(row.zone),
    audience: Number(row.audience ?? 0),
  }));

  const totalPublicaciones = tableByZone.reduce(
    (acc, row) => acc + Number(row.totalNotes ?? 0),
    0,
  );
  const totalPositivas = tableByZone.reduce(
    (acc, row) => acc + Number(row[NoteSentiment.POSITIVO] ?? 0),
    0,
  );
  const totalNeutras = tableByZone.reduce(
    (acc, row) => acc + Number(row[NoteSentiment.NEUTRO] ?? 0),
    0,
  );
  const totalNegativas = tableByZone.reduce(
    (acc, row) => acc + Number(row[NoteSentiment.NEGATIVO] ?? 0),
    0,
  );
  const totalAudiencia = tableByZone.reduce(
    (acc, row) => acc + Number(row.audience ?? 0),
    0,
  );

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
        encode: { x: "zone", y: "audience" },
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
        tooltip: (datum: { zone: string; audience: number }) => ({
          name: "Audiencia",
          value: datum.audience,
        }),
      },
      {
        type: "interval" as const,
        data: barData,
        encode: { x: "zone", y: "value", color: "sentiment" },
        transform: [{ type: "dodgeX" as const }],
        scale: {
          color: {
            domain: sentimentOrder,
            range: sentimentColors,
          },
        },
        style: {
          maxWidth: 14,
        },
        labels: [
          {
            text: (d: { value: number }) =>
              d.value > 0 ? String(d.value) : "",
            position: "top",
            fill: "#262626",
            fontSize: 10,
          },
        ],
        axis: {
          y: { title: "Publicaciones", position: "left" },
        },
        legend: { color: { title: false } },
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
        Publicaciones de notas por regiones
      </div>

      <div style={{ height: "250px" }}>
        <DualAxes {...dualConfig} autoFit />
      </div>

      <div
        style={{
          flex: 1,
          display: "flex",
          gap: 12,
          marginTop: 12,
          overflow: "hidden",
        }}
      >
        <div
          style={{
            flex: 2,
            display: "flex",
            flexDirection: "column",
            justifyContent: "flex-start",
            fontSize: 16,
            lineHeight: 1.4,
          }}
        >
          <div style={{ ...DASHBOARD_THEME.titleStyle, fontSize: 20 }}>
            Total publicaciones {totalPublicaciones.toLocaleString("es-CO")}
          </div>
          <div style={{ color: NoteSentimentColor.POSITIVO }}>
            Positivas {totalPositivas.toLocaleString("es-CO")}
          </div>
          <div style={{ color: NoteSentimentColor.NEUTRO }}>
            Neutras {totalNeutras.toLocaleString("es-CO")}
          </div>
          <div style={{ color: NoteSentimentColor.NEGATIVO }}>
            Negativas {totalNegativas.toLocaleString("es-CO")}
          </div>
        </div>

        <div
          style={{
            flex: 2,
            display: "flex",
            flexDirection: "column",
            justifyContent: "flex-start",
            color: "#3C357B",
            textAlign: "center",
          }}
        >
          <div style={{ fontSize: 20, lineHeight: 1.3 }}>
            Potencial audiencia alcanzada
          </div>
          <div
            style={{
              color: "#000",
              fontSize: 28,
              fontWeight: 700,
              lineHeight: 1.2,
              marginTop: 10,
            }}
          >
            {totalAudiencia.toLocaleString("es-CO")}
          </div>
        </div>

        <div style={{ flex: 1 }} />
      </div>
    </div>
  );
};

export default SectionByZone;
