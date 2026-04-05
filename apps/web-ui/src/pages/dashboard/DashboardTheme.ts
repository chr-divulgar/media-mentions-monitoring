/** Paleta centralizada del dashboard — cambiar aquí afecta todas las secciones */
export const DASHBOARD_THEME = {
  /** Fondo de cada tarjeta / sección */
  sectionBg: "#fff",
  /** Color de los títulos de sección */
  titleColor: "#00684d",
  /** Fondo de la slide en el PPT (hex sin #) */
  slideBgHex: "FFFFFF",
  /** Estilo del renglón de fecha (aplica a todas las secciones) */
  dateStyle: {
    background: "#fff",
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

/** Configuración base para gráficos de tipo Pie */
export const getPieConfig = (data: { type: string; value: number }[]) => ({
  appendPadding: 10,
  data,
  angleField: "value",
  colorField: "type",
  radius: 1,
  label: {
    content: ({ type, percent }: { type: string; percent: number }) =>
      `${type}: ${(percent * 100).toFixed(1)}%`,
  },
  interactions: [{ type: "element-active" }],
});
