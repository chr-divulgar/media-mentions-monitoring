// Reading the monthly notes Excel: which column is which, whatever its exact name ("Fecha" or "Feha", "Titular" or
// "Título", "Sub-temas"...). Pure functions, no xlsx: the page passes the header row and the cells.

export type ImportField =
  | "date"
  | "media"
  | "mediaName"
  | "topic"
  | "subtopic"
  | "subsubtopic"
  | "origin"
  | "department"
  | "zone"
  | "city"
  | "title"
  | "summary"
  | "rate"
  | "sentiment"
  | "value"
  | "audience"
  | "link";

// Every name a column has had in the client's files, already normalized (see normalizeHeader).
const ALIASES: Record<ImportField, string[]> = {
  date: ["fecha", "feha"],
  media: ["tipo de medio"],
  mediaName: ["medio"],
  topic: ["variables"],
  subtopic: ["tema"],
  subsubtopic: ["subtemas", "sub temas"],
  origin: ["origen"],
  department: ["departamento"],
  zone: ["zona"],
  city: ["ciudad"],
  title: ["titular", "titulo"],
  summary: ["resumen"],
  rate: ["tarifa"],
  sentiment: ["sentimiento"],
  value: ["valor"],
  audience: ["audiencia"],
  link: ["link", "enlace"],
};

// Without these a row is not a note; a sheet without their columns is a summary or a pivot table.
export const REQUIRED_FIELDS: { field: ImportField; label: string }[] = [
  { field: "date", label: "Fecha" },
  { field: "media", label: "Tipo de medio" },
  { field: "mediaName", label: "Medio" },
  { field: "title", label: "Titular" },
];

// Lowercase, no accents, any run of other characters as one space: "Sub-temas " is "sub temas".
export function normalizeHeader(header: unknown): string {
  return String(header ?? "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

// Field -> column index in the header row. A field whose column is not there is left out.
export function mapHeaders(headers: unknown[]): Partial<Record<ImportField, number>> {
  const normalized = headers.map(normalizeHeader);
  const columns: Partial<Record<ImportField, number>> = {};
  for (const [field, names] of Object.entries(ALIASES) as [ImportField, string[]][]) {
    const index = normalized.findIndex((header) => names.includes(header));
    if (index >= 0) columns[field] = index;
  }
  return columns;
}

// The labels of the required columns a sheet lacks; empty when it can be imported.
export function missingRequired(columns: Partial<Record<ImportField, number>>): string[] {
  return REQUIRED_FIELDS.filter(({ field }) => columns[field] === undefined).map(({ label }) => label);
}

// An Excel date serial (days since 1899-12-30) as YYYY-MM-DD, computed in UTC so no time zone moves the day.
export function excelSerialToIsoDate(serial: number): string {
  return new Date(Date.UTC(1899, 11, 30) + Math.floor(serial) * 86400000).toISOString().slice(0, 10);
}
