import * as JSZip from 'jszip';
import {
  buildDailyReport,
  dailyReportFileName,
  groupByMedia,
  groupByTheme,
  ReportNote,
  reportPeriodTitle,
} from './daily-report';

// A 1x1 PNG, as a client's logo would be stored (data URL).
const PNG =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

const note = (fields: Partial<ReportNote>): ReportNote => ({
  mediaName: 'Infobae',
  title: 'Titular',
  summary: 'Resumen',
  attachment: 'https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-titular.pdf',
  ...fields,
});

async function xmlOf(buffer: Buffer) {
  const zip = await JSZip.loadAsync(buffer);
  const read = async (name: string) => (await zip.file(name)?.async('string')) ?? '';
  return {
    document: await read('word/document.xml'),
    rels: await read('word/_rels/document.xml.rels'),
    headers: (await Promise.all(Object.keys(zip.files).filter((n) => /^word\/header\d*\.xml$/.test(n)).map(read))).join(''),
    media: Object.keys(zip.files).filter((n) => n.startsWith('word/media/')),
  };
}

// The text a reader sees, without the XML around it.
const text = (xml: string) => [...xml.matchAll(/<w:t(?:\s[^>]*)?>([^<]*)<\/w:t>/g)].map((m) => m[1]).join('|');

describe('reportPeriodTitle', () => {
  it('writes the two days the way the report does, with the year as 2.026', () => {
    expect(reportPeriodTitle('2026-10-22', '2026-10-23')).toBe('Octubre 22 / octubre 23 de 2.026');
  });

  it('writes the report day and the next day when the range has one day', () => {
    expect(reportPeriodTitle('2026-10-23', '2026-10-23')).toBe('Octubre 23 / octubre 24 de 2.026');
  });

  it('names both months when the range crosses one, and both years when it crosses one', () => {
    expect(reportPeriodTitle('2026-10-31', '2026-11-01')).toBe('Octubre 31 / noviembre 1 de 2.026');
    expect(reportPeriodTitle('2026-12-31', '2027-01-01')).toBe('Diciembre 31 de 2.026 / enero 1 de 2.027');
    expect(reportPeriodTitle('2026-12-31', '2026-12-31')).toBe('Diciembre 31 de 2.026 / enero 1 de 2.027');
  });
});

describe('dailyReportFileName', () => {
  it('is named after the client and the last day, like the manual one', () => {
    expect(dailyReportFileName('Ecopetrol', '2026-10-23')).toBe('Informe Ecopetrol-23-10-26.docx');
  });
});

describe('groupByTheme', () => {
  it('groups by subtopic, falls back to the topic and then to "Otros", themes A-Z and notes in the order they were registered', () => {
    const groups = groupByTheme([
      note({ title: 'c', subtopic: 'gas', index: '3' }),
      note({ title: 'a', subtopic: 'Carlos Suarez', index: '1' }),
      note({ title: 'd', topic: 'Finanzas', index: '4' }),
      note({ title: 'b', subtopic: 'gas', index: '2' }),
      note({ title: 'e', index: '5' }),
      note({ title: 'f', subtopic: 'Carlos Suarez', index: '6' }),
    ]);

    expect(groups.map((g) => [g.theme, g.notes.map((n) => n.title).join('')])).toEqual([
      ['Carlos Suarez', 'af'],
      ['Finanzas', 'd'],
      ['gas', 'bc'],
      ['Otros', 'e'],
    ]);
  });

  it('orders notes without a consecutive by their time', () => {
    const groups = groupByTheme([
      note({ title: 'late', subtopic: 'x', startTime: '2026-10-23T15:00:00Z' }),
      note({ title: 'early', subtopic: 'x', startTime: '2026-10-23T08:00:00Z' }),
    ]);

    expect(groups[0].notes.map((n) => n.title)).toEqual(['early', 'late']);
  });

  it('orders the notes of a theme A-Z by outlet, then in registered order', () => {
    const groups = groupByTheme([
      note({ title: 'p', mediaName: 'Portafolio', subtopic: 'x', index: '1' }),
      note({ title: 't2', mediaName: 'El Tiempo', subtopic: 'x', index: '3' }),
      note({ title: 'm', mediaName: 'el Meridiano', subtopic: 'x', index: '4' }),
      note({ title: 't1', mediaName: 'El Tiempo', subtopic: 'x', index: '2' }),
    ]);

    expect(groups[0].notes.map((n) => n.title)).toEqual(['m', 't1', 't2', 'p']);
  });
});

describe('groupByMedia', () => {
  it('groups notes by media and keeps a stable section order', () => {
    const groups = groupByMedia([
      note({ media: 'internet', title: 'i' }),
      note({ media: 'tv', title: 't' }),
      note({ media: 'radio', title: 'r' }),
      note({ media: 'televisión', title: 'tv-es' }),
    ]);

    expect(groups.map((group) => group.label)).toEqual(['Radio', 'Televisión', 'Internet']);
    expect(groups[1].themes.flatMap((theme) => theme.notes.map((item) => item.title))).toEqual(['t', 'tv-es']);
  });
});

describe('buildDailyReport', () => {
  const notes = [
    note({ media: 'internet', mediaName: 'Investing.com', title: 'Ecopetrol nombra vicepresidente', summary: 'Ecopetrol S.A. anunció hoy…', subtopic: 'nombramientos en Ecopetrol', index: '1' }),
    note({
      media: 'radio',
      mediaName: 'Infobae',
      title: 'Salida de Rodrigo Lara',
      summary: 'La salida del titular…',
      subtopic: 'Carlos Suarez',
      index: '2',
      attachment: 'https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-salida.pdf',
      link: 'https://chrmonitoreo.org/colombia/ecopetrol/2026/10/23/nota-2.mp3',
    }),
    note({ media: 'tv', mediaName: 'Pulzo', title: 'Sin PDF', summary: 'r', subtopic: 'Carlos Suarez', index: '3', attachment: undefined, link: 'https://chrmonitoreo.com/colombia/ecopetrol/2026/10/23/nota-3.mp4' }),
  ];

  it('has the title, the period, the indicators and each theme with its notes, outlet, linked headline and summary', async () => {
    const buffer = await buildDailyReport({
      client: {
        name: 'Ecopetrol',
        reportLogo: PNG,
        reportIndicators: [
          { source: 'El Tiempo', label: 'Divisas' },
          { source: 'La República', label: 'Movimiento accionario', url: 'https://larepublica.co/acciones' },
        ],
      },
      from: '2026-10-22',
      to: '2026-10-23',
      notes,
    });
    const xml = await xmlOf(buffer);
    const seen = text(xml.document);

    expect(seen).toContain('Reporte de Noticias');
    expect(seen).toContain('Octubre 22 / octubre 23 de 2.026');
    expect(seen).toContain('Indicadores');
    expect(seen).toContain('El Tiempo');
    expect(seen).toContain('Divisas');
    // Media sections are stable: Radio -> Televisión -> Internet.
    expect(seen.indexOf('Medio: Radio')).toBeLessThan(seen.indexOf('Medio: Televisión'));
    expect(seen.indexOf('Medio: Televisión')).toBeLessThan(seen.indexOf('Medio: Internet'));
    expect(seen).toContain('Investing.com|Ecopetrol nombra vicepresidente|Ecopetrol S.A. anunció hoy…');
    expect(seen).toContain('Infobae|Salida de Rodrigo Lara|La salida del titular…');
    // Radio notes link only to the generated archive link (FileZilla flow).
    expect(xml.rels).toContain('https://chrmonitoreo.org/colombia/ecopetrol/2026/10/23/nota-2.mp3');
    expect(xml.rels).not.toContain('https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-salida.pdf');
    expect(xml.rels).toContain('https://chrmonitoreo.com/colombia/ecopetrol/2026/10/23/nota-3.mp4');
    expect(xml.rels).toContain('https://larepublica.co/acciones');
    // The stray "A" lines of the hand-made report are not there.
    expect(seen.split('|')).not.toContain('A');
    // The client's logo heads every page.
    expect(xml.headers).toContain('<a:blip');
    expect(xml.media.length).toBeGreaterThan(0);
    // The look of the original: Arial 12 (24 half-points).
    expect(xml.document).toContain('w:ascii="Arial"');
    expect(xml.document).toContain('w:sz w:val="24"');
  });

  it('comes out without logo or indicators when the client has none', async () => {
    const xml = await xmlOf(await buildDailyReport({ client: { name: 'Otro' }, from: '2026-10-23', to: '2026-10-23', notes }));

    expect(text(xml.document)).not.toContain('Indicadores');
    expect(xml.media).toHaveLength(0);
    expect(text(xml.document)).toContain('Octubre 23 / octubre 24 de 2.026');
  });

  it('uses the public clip link when attachment is not an URL', async () => {
    const xml = await xmlOf(await buildDailyReport({
      client: { name: 'Ecopetrol' },
      from: '2026-10-23',
      to: '2026-10-23',
      notes: [note({
        media: 'radio',
        mediaName: 'Blu Radio',
        title: 'Nota de hoy',
        attachment: 'NOTA_12_BLU_23-10-26.mp3',
        link: 'https://chrmonitoreo.org/colombia/ecopetrol/2026/10/23/nota-12.mp3',
      })],
    }));

    expect(xml.rels).toContain('https://chrmonitoreo.org/colombia/ecopetrol/2026/10/23/nota-12.mp3');
    expect(xml.rels).not.toContain('NOTA_12_BLU_23-10-26.mp3');
  });

  it('does not hyperlink a radio note when the generated link is missing', async () => {
    const xml = await xmlOf(await buildDailyReport({
      client: { name: 'Ecopetrol' },
      from: '2026-10-23',
      to: '2026-10-23',
      notes: [note({
        media: 'radio',
        mediaName: 'Blu Radio',
        title: 'Sin link generado',
        attachment: 'https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/algo.pdf',
        link: undefined,
      })],
    }));

    expect(xml.rels).not.toContain('https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/algo.pdf');
  });

  it('does not hyperlink an internet note when the archived PDF link is missing', async () => {
    const xml = await xmlOf(await buildDailyReport({
      client: { name: 'Ecopetrol' },
      from: '2026-10-23',
      to: '2026-10-23',
      notes: [note({
        media: 'internet',
        mediaName: 'Portal X',
        title: 'Sin PDF archivado',
        attachment: undefined,
        link: 'https://portal-x.com/nota',
      })],
    }));

    expect(xml.rels).not.toContain('https://portal-x.com/nota');
  });
});
