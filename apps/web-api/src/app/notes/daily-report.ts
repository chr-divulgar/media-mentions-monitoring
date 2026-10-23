import {
  AlignmentType,
  Document,
  ExternalHyperlink,
  Header,
  ImageRun,
  LevelFormat,
  Packer,
  Paragraph,
  TextRun,
} from 'docx';

// The daily news report of a client (the "Reporte de Noticias" the team used to put together by hand in Word): its
// internet notes of a date range, grouped by theme, each with its outlet, its headline linked to the archived PDF and
// its summary. Pure: the caller hands in the notes and the client's logo and indicators.

export interface ReportNote {
  mediaName?: string;
  title?: string;
  summary?: string;
  // The archived PDF of the note (its permanent copy); the article's own link when there is none.
  attachment?: string;
  link?: string;
  topic?: string;
  subtopic?: string;
  // The NOTA consecutive: the order the notes were registered in.
  index?: string;
  startTime?: string;
}

export interface ReportIndicator {
  source: string;
  label: string;
  url?: string;
}

export interface ReportClient {
  name: string;
  // A PNG or JPEG as a data URL.
  reportLogo?: string;
  reportIndicators?: ReportIndicator[];
}

const MONTHS = [
  'enero', 'febrero', 'marzo', 'abril', 'mayo', 'junio',
  'julio', 'agosto', 'septiembre', 'octubre', 'noviembre', 'diciembre',
];

const THEME_COLOR = '1F3864';
const LINK_COLOR = '0000FF';
const FONT = 'Arial';
const SIZE = 24; // half-points: 12 pt

const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);

// 2026 → "2.026", as the report writes it.
const yearText = (year: number) => year.toLocaleString('es-CO').replace(/\s|,/g, '.');

const parts = (day: string) => {
  const [year, month, date] = day.split('-').map(Number);
  return { year, month: MONTHS[month - 1], date };
};

/** "Octubre 22 / octubre 23 de 2.026": the first and last day of the report. */
export function reportPeriodTitle(from: string, to: string): string {
  const a = parts(from);
  const b = parts(to);
  if (from === to) return capitalize(`${b.month} ${b.date} de ${yearText(b.year)}`);
  if (a.year !== b.year) {
    return capitalize(`${a.month} ${a.date} de ${yearText(a.year)} / ${b.month} ${b.date} de ${yearText(b.year)}`);
  }
  return capitalize(`${a.month} ${a.date} / ${b.month} ${b.date} de ${yearText(b.year)}`);
}

/** "Informe Ecopetrol-23-10-26.docx", after the last day of the report. */
export function dailyReportFileName(client: string, to: string): string {
  const [year, month, day] = to.split('-');
  return `Informe ${client}-${day}-${month}-${year.slice(2)}.docx`;
}

const registeredOrder = (a: ReportNote, b: ReportNote): number => {
  const ai = Number(a.index);
  const bi = Number(b.index);
  if (Number.isFinite(ai) && Number.isFinite(bi) && a.index && b.index && ai !== bi) return ai - bi;
  return new Date(a.startTime ?? 0).getTime() - new Date(b.startTime ?? 0).getTime();
};

/** The notes by theme (the subtopic; else the topic; else "Otros"), themes in the order of their first note. */
export function groupByTheme(notes: ReportNote[]): { theme: string; notes: ReportNote[] }[] {
  const groups = new Map<string, ReportNote[]>();
  for (const item of [...notes].sort(registeredOrder)) {
    const theme = item.subtopic?.trim() || item.topic?.trim() || 'Otros';
    groups.set(theme, [...(groups.get(theme) ?? []), item]);
  }
  return [...groups.entries()].map(([theme, grouped]) => ({ theme, notes: grouped }));
}

const run = (text: string, options: { bold?: boolean; color?: string } = {}) =>
  new TextRun({ text, font: FONT, size: SIZE, bold: options.bold, color: options.color });

const blank = () => new Paragraph({ children: [] });

const link = (url: string, text: string, bold = true) =>
  new ExternalHyperlink({
    link: url,
    children: [new TextRun({ text, font: FONT, size: SIZE, bold, color: LINK_COLOR, underline: {} })],
  });

// The logo at a fixed width, keeping its proportions (read from the PNG or JPEG header).
function logoRun(dataUrl: string): ImageRun | null {
  const match = /^data:image\/(png|jpe?g);base64,(.+)$/i.exec(dataUrl);
  if (!match) return null;
  const data = Buffer.from(match[2], 'base64');
  const type = match[1].toLowerCase() === 'png' ? 'png' : 'jpg';
  const size = type === 'png' ? pngSize(data) : jpegSize(data);
  const width = 240;
  const height = size ? Math.max(1, Math.round((width * size.height) / size.width)) : 80;
  return new ImageRun({ type, data, transformation: { width, height } });
}

function pngSize(data: Buffer): { width: number; height: number } | null {
  return data.length >= 24 ? { width: data.readUInt32BE(16), height: data.readUInt32BE(20) } : null;
}

function jpegSize(data: Buffer): { width: number; height: number } | null {
  let offset = 2;
  while (offset + 9 < data.length) {
    if (data[offset] !== 0xff) return null;
    const marker = data[offset + 1];
    const length = data.readUInt16BE(offset + 2);
    // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the picture's size.
    if (marker >= 0xc0 && marker <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(marker)) {
      return { height: data.readUInt16BE(offset + 5), width: data.readUInt16BE(offset + 7) };
    }
    offset += 2 + length;
  }
  return null;
}

/** The report as a .docx file. */
export async function buildDailyReport(input: {
  client: ReportClient;
  from: string;
  to: string;
  notes: ReportNote[];
}): Promise<Buffer> {
  const { client, from, to, notes } = input;
  const body: Paragraph[] = [
    new Paragraph({ alignment: AlignmentType.CENTER, children: [run('Reporte de Noticias', { bold: true })] }),
    blank(),
    new Paragraph({ alignment: AlignmentType.CENTER, children: [run(reportPeriodTitle(from, to), { bold: true })] }),
    blank(),
  ];

  const indicators = (client.reportIndicators ?? []).filter((item) => item.source?.trim() || item.label?.trim());
  if (indicators.length > 0) {
    body.push(new Paragraph({ children: [run('Indicadores', { bold: true, color: THEME_COLOR })] }));
    for (const indicator of indicators) {
      const label = indicator.label?.trim() ?? '';
      body.push(
        new Paragraph({
          numbering: { reference: 'bullets', level: 0 },
          children: [
            run(`${indicator.source.trim()} `, { bold: true }),
            indicator.url?.trim() ? link(indicator.url.trim(), label) : run(label, { bold: true, color: THEME_COLOR }),
          ],
        }),
      );
    }
    body.push(blank());
  }

  for (const group of groupByTheme(notes)) {
    body.push(new Paragraph({ children: [run(`Temática: ${group.theme}`, { bold: true, color: THEME_COLOR })] }), blank());
    for (const item of group.notes) {
      const url = item.attachment?.trim() || item.link?.trim();
      const title = item.title?.trim() || 'Sin titular';
      body.push(
        new Paragraph({ children: [run(item.mediaName?.trim() || 'Sin medio', { bold: true })] }),
        new Paragraph({ children: [url ? link(url, title) : run(title, { bold: true })] }),
      );
      if (item.summary?.trim()) body.push(new Paragraph({ children: [run(item.summary.trim())] }));
      body.push(blank());
    }
  }

  const logo = client.reportLogo ? logoRun(client.reportLogo) : null;
  const document = new Document({
    creator: 'CHR Divulgar',
    title: `Reporte de Noticias ${client.name}`,
    styles: { default: { document: { run: { font: FONT, size: SIZE } } } },
    numbering: {
      config: [
        {
          reference: 'bullets',
          levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 720, hanging: 360 } } } }],
        },
      ],
    },
    sections: [
      {
        properties: {
          // Letter, with the margins of the hand-made report: 1" top and bottom, 0.75" at the sides.
          page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, bottom: 1440, left: 1080, right: 1080 } },
        },
        headers: logo
          ? { default: new Header({ children: [new Paragraph({ alignment: AlignmentType.CENTER, children: [logo] })] }) }
          : undefined,
        children: body,
      },
    ],
  });

  return Packer.toBuffer(document);
}
