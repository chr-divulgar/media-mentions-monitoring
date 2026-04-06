/** Paleta centralizada del dashboard — cambiar aquí afecta todas las secciones */
import { NoteSentimentColor } from "@repo/shared";

export const DASHBOARD_THEME = {
  /** Fondo de cada tarjeta / sección */
  sectionBg: "#fff",
  /** Color de los títulos de sección */
  titleColor: "#00684d",
  /** Fondo de la slide en el PPT (hex sin #) */
  slideBgHex: "FFFFFF",
  /** Estilo del renglón de fecha (aplica a todas las secciones) */
  dateStyle: {
    color: "#989898",
    padding: "6px 0",
    textAlign: "left" as const,
    borderRadius: 4,
    fontSize: 12,
    marginBottom: 4,
    letterSpacing: 0.5,
  },
  /** Estilo de los títulos de sección (aplica a todas las secciones) */
  titleStyle: {
    color: "#00684d",
    fontWeight: 700,
    fontSize: 32,
    textAlign: "left" as const,
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
  },
  legend: { color: { title: false } },
  interactions: [{ type: "element-active" }],
});
