/**
 * Paleta centralizada del dashboard — cambiar aquí afecta todas las secciones.
 *
 * Tipografías y colores extraídos directamente del PPT original
 * ("Análisis Monitoreo ... nuevo formato.pptx"). Para Positiva/Negativa/Neutra el PPT usa
 * variantes distintas según el gráfico (tabla vs. pie vs. barras) — se fijó un único hex por
 * sentimiento (el más repetido en el documento) para que no haya casos especiales por sección.
 */
export const DASHBOARD_THEME = {
  /** Fondo de cada tarjeta / sección */
  sectionBg: "#fff",

  /** Fondo de la slide en el PPT (hex sin #) */
  slideBgHex: "FFFFFF",

  /** Fuente de títulos, números KPI y énfasis en negrita (confirmada en el PPT) */
  headingFontFamily: "'Arial Black', Arial, sans-serif",
  /** Fuente de cuerpo / tablas (confirmada en el PPT) */
  bodyFontFamily: "Arial, sans-serif",

  /** Estilo del renglón de fecha (aplica a todas las secciones) */
  dateStyle: {
    color: "#7f7f7f",
    fontSize: 12,
    fontStyle: "italic",
    fontFamily: "Arial, sans-serif",
  },
  /** Estilo de los títulos de sección (aplica a todas las secciones) */
  titleStyle: {
    color: "#00323f",
    fontWeight: "bold",
    fontSize: 40,
    textAlign: "left" as const,
    lineHeight: 1.2,
    fontFamily: "'Arial Black', Arial, sans-serif",
  },
  /** Estilo de contenedor de cada sección (slide-like card) */
  sectionContainer: {
    position: "relative" as const,
    margin: "32px auto",
    width: 960,
    height: 540,
    background: "#fff",
    borderRadius: 8,
    padding: 24,
    boxSizing: "border-box" as const,
    overflow: "hidden" as const,
  },

  /** Fondo de encabezado de tabla / fila divisora de grupo (ej. "Grupo Empresarial") */
  headerBg: "#9BC2E6",
  /** Color de borde de celda de tabla */
  borderColor: "#000000",

  /**
   * Color único y fijo por sentimiento, usado en todo el dashboard (fondo de celda de tabla,
   * pie chart, barras, línea de tendencia) — el más repetido en el PPT original para cada
   * sentimiento, sin variantes por sección/gráfico.
   */
  SENTIMENT_COLORS: {
    positiva: "#27895A",
    negativa: "#FF7C80",
    neutra: "#bfbfbf",
  },

  /** Pie de página heredado del slide-master del PPT ("ECP-INFORMACION PUBLICA") */
  footerStyle: {
    position: "absolute" as const,
    left: "46%",
    bottom: 2,
    fontSize: 8,
    color: "rgba(0,0,0,0.5)",
    fontFamily: "Calibri, Arial, sans-serif",
  },
  /** Texto del pie de página, idéntico en todas las secciones */
  footerText: "ECP-INFORMACION PUBLICA",
} as const;

/** Color de fondo fijo para un sentimiento (Positiva/Negativa/Neutra), sin importar el contexto */
export const getSentimentColor = (type: string) => {
  const normalizedType = type.trim().toLowerCase();
  const foundKey = Object.keys(DASHBOARD_THEME.SENTIMENT_COLORS).find(
    (key) => key.toLowerCase() === normalizedType,
  );
  return foundKey
    ? DASHBOARD_THEME.SENTIMENT_COLORS[
        foundKey as keyof typeof DASHBOARD_THEME.SENTIMENT_COLORS
      ]
    : "#999";
};

/**
 * Color de texto legible sobre un fondo de `SENTIMENT_COLORS`: blanco solo sobre el fondo oscuro
 * (positiva), negro sobre los fondos claros (negativa/neutra). Regla genérica de contraste, no
 * una excepción por caso.
 */
export const getSentimentTextColor = (bg: string) =>
  bg === DASHBOARD_THEME.SENTIMENT_COLORS.positiva ? "#fff" : "#000";

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
