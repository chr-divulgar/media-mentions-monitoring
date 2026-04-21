/** Paleta centralizada del dashboard — cambiar aquí afecta todas las secciones */
import { NoteSentimentColor } from "@repo/shared";

export const DASHBOARD_THEME = {
  /** Fondo de cada tarjeta / sección */
  sectionBg: "#fff",

  /** Fondo de la slide en el PPT (hex sin #) */
  slideBgHex: "FFFFFF",
  /** Estilo del renglón de fecha (aplica a todas las secciones) */
  dateStyle: {
    color: "#7f7f7f",
    fontSize: 12,
    fontStyle: "italic",
  },
  /** Estilo de los títulos de sección (aplica a todas las secciones) */
  titleStyle: {
    color: "#00323f",
    fontWeight: "bold",
    fontSize: 40,
    textAlign: "left" as const,
    lineHeight: 1.2,
  },
  /** Estilo de contenedor de cada sección (slide-like card) */
  sectionContainer: {
    margin: "32px auto",
    width: 960,
    height: 540,
    background: "#fff",
    borderRadius: 8,
    padding: 24,
    boxSizing: "border-box" as const,
    overflow: "hidden" as const,
  },
} as const;

const SENTIMENT_COLOR_MAP: Record<string, string> = {
  Negativa: NoteSentimentColor.NEGATIVO,
  Neutra: NoteSentimentColor.NEUTRO,
  Positiva: NoteSentimentColor.POSITIVO,
};

const getSentimentColor = (type: string) => {
  const normalizedType = type.trim().toLowerCase();
  const foundKey = Object.keys(SENTIMENT_COLOR_MAP).find(
    (key) => key.toLowerCase() === normalizedType,
  );
  return foundKey ? SENTIMENT_COLOR_MAP[foundKey] : "#999";
};

/** Configuración base para gráficos de tipo Pie (API @ant-design/plots v2) */
export const getPieConfig = (data: { type: string; value: number }[]) => ({
  data,
  angleField: "value",
  colorField: "type",
  radius: 0.85,
  scale: {
    color: {
      domain: data.map((item) => item.type),
      range: data.map((item) => getSentimentColor(item.type)),
    },
  },
  label: {
    text: (d: { type: string; value: number }) => {
      const total = data.reduce((s, c) => s + c.value, 0);
      const pct = total > 0 ? ((d.value / total) * 100).toFixed(1) : "0.0";
      return `${pct}%`;
    },
    style: { fontSize: 12 },
    position: "outside",
    autoRotate: false,
    autoHide: false,
  },
  legend: { color: { title: false } },
  interactions: [{ type: "element-active" }],
});
