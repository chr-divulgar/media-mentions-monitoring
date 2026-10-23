import * as fs from 'fs';
import { NotesService } from './notes.service';

// Minimal in-memory stand-ins for the two Mongo repositories the service reads and writes.
function build(
  alert: Record<string, unknown> | null,
  existingNote: Record<string, unknown> | null = null,
  clients: unknown = {},
) {
  const saved: Record<string, unknown>[] = [];
  const noteRepo = {
    find: jest.fn().mockResolvedValue(existingNote ? [existingNote] : []),
    findOne: jest.fn().mockResolvedValue(existingNote),
    findOneBy: jest.fn().mockResolvedValue(existingNote),
    create: jest.fn((fields: Record<string, unknown>) => ({ ...fields })),
    save: jest.fn(async (note: Record<string, unknown>) => {
      saved.push(note);
      return note;
    }),
  };
  const workerAlertRepo = { findOneBy: jest.fn().mockResolvedValue(alert) };
  const dataSource = {
    getMongoRepository: (entity: { name: string }) =>
      entity.name === 'WorkerAlert' ? workerAlertRepo : noteRepo,
  };
  const service = new NotesService(dataSource as never, clients as never, {} as never);
  return { service, saved };
}

const ALERT_ID = '507f1f77bcf86cd799439011';
const alert = {
  media: 'internet',
  url: 'https://site.co/a',
  title: 'Ecopetrol culmina su campaña',
  text: 'Primer párrafo.',
  platform: 'Runrun',
  clientName: 'Ecopetrol',
  words: ['Ecopetrol', 'Cenit'],
  startTime: new Date('2026-10-06T00:26:28Z'),
};

const workerReply = (body: unknown, ok = true, status = 200) => ({
  ok,
  status,
  json: async () => body,
});

describe('internet notes', () => {
  const fetchMock = jest.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    global.fetch = fetchMock as never;
  });

  describe('previewInternetNote', () => {
    it('asks the worker for a preview only: nothing is saved and nothing is archived', async () => {
      fetchMock.mockResolvedValue(workerReply({ previewId: 'p1', link: 'https://files.test/runrun-01-x.pdf' }));
      const { service, saved } = build(alert);

      const preview = await service.previewInternetNote(ALERT_ID);

      expect(fetchMock.mock.calls[0][0]).toMatch(/\/notes\/pdf\/preview$/);
      expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toMatchObject({
        outlet: 'Runrun',
        title: alert.title,
        url: alert.url,
        date: '2026-10-05T19:26:28-05:00',
        // The PDF is filed under the client's folder.
        client: 'Ecopetrol',
        // The words the alert matched come out highlighted in yellow in the PDF.
        keywords: ['Ecopetrol', 'Cenit'],
        // So the worker can use the PDF it made when the alert was created, instead of printing the page again.
        alertId: ALERT_ID,
      });
      expect(preview).toMatchObject({
        previewId: 'p1',
        pdfLink: 'https://files.test/runrun-01-x.pdf',
        note: { title: alert.title, summary: alert.text, program: 'Runrun', link: alert.url },
      });
      expect(saved).toHaveLength(0);
    });

    it('highlights the client\'s own keywords when the alert has no matched words (one left for manual review)', async () => {
      fetchMock.mockResolvedValue(workerReply({ previewId: 'p1', link: 'https://files.test/x.pdf' }));
      const clients = {
        getClients: jest.fn().mockResolvedValue([
          { name: 'Otro', words: [{ value: 'Nada' }] },
          { name: 'Ecopetrol', words: [{ value: 'Ecopetrol S.A.' }, { value: ' ' }, { value: 'Cenit' }] },
        ]),
      };
      const { service } = build({ ...alert, words: [] }, null, clients);

      await service.previewInternetNote(ALERT_ID);

      expect(JSON.parse(fetchMock.mock.calls[0][1].body).keywords).toEqual(['Ecopetrol S.A.', 'Cenit']);
    });

    it('still prints when the client\'s keywords cannot be read', async () => {
      fetchMock.mockResolvedValue(workerReply({ previewId: 'p1', link: 'https://files.test/x.pdf' }));
      const clients = { getClients: jest.fn().mockRejectedValue(new Error('mongo down')) };
      const { service } = build({ ...alert, words: [] }, null, clients);

      await service.previewInternetNote(ALERT_ID);

      expect(JSON.parse(fetchMock.mock.calls[0][1].body).keywords).toEqual([]);
    });

    it('offers what the user already edited on an earlier pass', async () => {
      fetchMock.mockResolvedValue(workerReply({ previewId: 'p1', link: 'https://files.test/x.pdf' }));
      const { service } = build(alert, { index: '12', title: 'Mi titular', summary: 'Mi resumen', program: 'Mi programa' });

      const { note } = await service.previewInternetNote(ALERT_ID);

      expect(note).toMatchObject({ index: '12', title: 'Mi titular', summary: 'Mi resumen', program: 'Mi programa' });
    });

    it('surfaces the worker message when the PDF cannot be printed', async () => {
      fetchMock.mockResolvedValue(workerReply({ message: 'No se pudo generar el PDF de la página.' }, false, 502));
      const { service } = build(alert);

      await expect(service.previewInternetNote(ALERT_ID)).rejects.toThrow('No se pudo generar el PDF');
    });

    it('rejects an alert that is not an internet one', async () => {
      const { service } = build({ ...alert, media: 'radio' });

      await expect(service.previewInternetNote(ALERT_ID)).rejects.toThrow('no es una nota de internet');
    });
  });

  describe('uploadInternetNotePdf', () => {
    const pdf = Buffer.from('%PDF-1.7 hecho a mano');

    it('sends the PDF the user made to the worker as the preview and returns the same draft as an automatic one', async () => {
      fetchMock.mockResolvedValue(workerReply({ previewId: 'p9', link: 'https://files.test/runrun-01-x.pdf' }));
      const { service, saved } = build(alert, { index: '12', title: 'Mi titular' });

      const preview = await service.uploadInternetNotePdf(ALERT_ID, pdf);

      const [url, init] = fetchMock.mock.calls[0];
      expect(url).toMatch(/\/notes\/pdf\/upload\?/);
      const query = new URL(url).searchParams;
      expect(Object.fromEntries(query)).toMatchObject({
        outlet: 'Runrun',
        title: alert.title,
        date: '2026-10-05T19:26:28-05:00',
        client: 'Ecopetrol',
      });
      expect(init.method).toBe('POST');
      expect(init.headers).toMatchObject({ 'Content-Type': 'application/pdf' });
      expect(init.body).toBe(pdf);
      expect(preview).toMatchObject({
        previewId: 'p9',
        pdfLink: 'https://files.test/runrun-01-x.pdf',
        note: { index: '12', title: 'Mi titular', link: alert.url },
      });
      expect(saved).toHaveLength(0);
    });

    it('surfaces the worker message when the file is not a usable PDF', async () => {
      fetchMock.mockResolvedValue(workerReply({ message: 'El archivo no es un PDF.' }, false, 502));
      const { service } = build(alert);

      await expect(service.uploadInternetNotePdf(ALERT_ID, pdf)).rejects.toThrow('El archivo no es un PDF.');
    });

    it('rejects an empty upload and an alert that is not an internet one', async () => {
      const { service } = build(alert);
      await expect(service.uploadInternetNotePdf(ALERT_ID, Buffer.alloc(0))).rejects.toThrow('PDF');

      const radio = build({ ...alert, media: 'radio' }).service;
      await expect(radio.uploadInternetNotePdf(ALERT_ID, pdf)).rejects.toThrow('no es una nota de internet');
      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe('saveInternetNote', () => {
    const dto = { alertId: ALERT_ID, previewId: 'p1', index: '10', program: 'Runrun', title: '  Mi titular ', summary: 'Resumen' };

    it('archives the previewed PDF only now and stores its link, with the Bogotá date', async () => {
      fetchMock.mockResolvedValue(workerReply({ link: 'https://files.test/runrun-01-x.pdf' }));
      const { service, saved } = build(alert);

      const note = await service.saveInternetNote(dto);

      expect(fetchMock.mock.calls[0][0]).toMatch(/\/notes\/pdf\/commit$/);
      expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ previewId: 'p1' });
      expect(note).toMatchObject({
        media: 'internet',
        mediaName: 'Runrun',
        clientName: 'Ecopetrol',
        date: '2026-10-05',
        link: alert.url,
        attachment: 'https://files.test/runrun-01-x.pdf',
        index: '10',
        title: 'Mi titular',
        summary: 'Resumen',
      });
      expect(saved).toHaveLength(1);
    });

    it('stores the classification and pricing the user chose', async () => {
      fetchMock.mockResolvedValue(workerReply({ link: 'https://files.test/runrun-01-x.pdf' }));
      const { service } = build(alert);

      const note = await service.saveInternetNote({
        ...dto,
        topic: 'Operación',
        subtopic: 'Producción',
        origin: 'Directa',
        sentiment: 'Positiva',
        zone: 'Centro',
        city: 'Bogotá',
        rate: '1000',
        audience: '5000',
        value: '1000',
      });

      expect(note).toMatchObject({
        topic: 'Operación',
        subtopic: 'Producción',
        origin: 'Directa',
        sentiment: 'Positiva',
        zone: 'Centro',
        city: 'Bogotá',
        rate: '1000',
        audience: '5000',
        value: '1000',
      });
    });

    it('saves nothing when the archive upload fails', async () => {
      fetchMock.mockResolvedValue(workerReply({ message: 'No se pudo subir el PDF al servidor de archivo.' }, false, 502));
      const { service, saved } = build(alert);

      await expect(service.saveInternetNote(dto)).rejects.toThrow('No se pudo subir el PDF');
      expect(saved).toHaveLength(0);
    });

    it('requires a headline', async () => {
      const { service } = build(alert);

      await expect(service.saveInternetNote({ ...dto, title: '  ' })).rejects.toThrow('titular');
      expect(fetchMock).not.toHaveBeenCalled();
    });
  });
});

describe('NotesService.setNote', () => {
  it('does not let the client overwrite a generated PDF link', async () => {
    const existing = { attachment: 'https://files.test/x.pdf', title: 'T', date: '2026-10-05', media: 'internet', mediaName: 'Runrun' };
    const { service, saved } = build(null, existing);

    await service.setNote({
      id: ALERT_ID, title: 'Nuevo', date: '2026-10-05', media: 'internet', mediaName: 'Runrun',
      attachment: 'otro.pdf', summary: 's', index: '10', program: 'p',
    });

    expect(saved[0]).toMatchObject({ title: 'Nuevo', attachment: 'https://files.test/x.pdf' });
  });

  it('completes the date, medium, outlet and client of a note made from an alert', async () => {
    const existing = { alert_id: ALERT_ID, title: 'T' };
    const radioAlert = { ...alert, media: 'radio', platform: 'Javeriana', startTime: new Date('2026-10-07T11:39:23Z') };
    const { service, saved } = build(radioAlert, existing);

    await service.setNote({
      id: ALERT_ID, title: 'Nuevo', startTime: '2026-10-07T11:39:23.590Z', summary: 's', index: '10', program: 'p', duration: 90,
    });

    expect(saved[0]).toMatchObject({
      title: 'Nuevo',
      media: 'radio',
      mediaName: 'Javeriana',
      clientName: 'Ecopetrol',
      date: '2026-10-07',
    });
  });

  it('keeps what the note already says over what the alert says', async () => {
    const existing = { alert_id: ALERT_ID };
    const { service, saved } = build({ ...alert, media: 'radio', platform: 'Javeriana' }, existing);

    await service.setNote({
      id: ALERT_ID, title: 'T', date: '2026-01-02', media: 'tv', mediaName: 'RCN', clientName: 'Otro',
    });

    expect(saved[0]).toMatchObject({ date: '2026-01-02', media: 'tv', mediaName: 'RCN', clientName: 'Otro' });
  });

  it('still rejects a note with no alert to complete it from', async () => {
    const { service } = build(null, { title: 'T' });

    await expect(service.setNote({ id: ALERT_ID, title: 'T' })).rejects.toThrow('Faltan campos obligatorios');
  });
});

describe('NotesService.archiveNoteClip', () => {
  const fetchMock = jest.fn();
  let existsSync: jest.SpyInstance;
  const present = (...names: string[]) =>
    existsSync.mockImplementation((p: unknown) => names.some((n) => String(p).replace(/\\/g, '/').endsWith(n)));

  const radioNote = {
    media: 'radio', mediaName: 'Blu Radio', clientName: 'Ecopetrol', date: '2026-10-14', startTime: '2026-10-14T15:30:48.000Z',
  };

  beforeEach(() => {
    fetchMock.mockReset().mockResolvedValue(workerReply({ path: 'colombia/ecopetrol/2026/10/14/x.mp3' }));
    global.fetch = fetchMock as never;
    existsSync = jest.spyOn(fs, 'existsSync');
  });
  afterEach(() => existsSync.mockRestore());

  it('files the mp3 of a radio note under its client, with the Bogotá moment of the clip', async () => {
    present('audioFiles/fragment_AL1.mp3');
    const { service } = build(null);

    expect(await service.archiveNoteClip(radioNote, 'AL1')).toBe(true);

    expect(fetchMock.mock.calls[0][0]).toMatch(/\/notes\/clip$/);
    const body = JSON.parse(fetchMock.mock.calls[0][1].body);
    expect(body).toMatchObject({
      medium: 'radio', client: 'Ecopetrol', platform: 'Blu Radio', date: '2026-10-14', stamp: '2026-10-14_10-30-48', alertId: 'AL1',
    });
    expect(body.filePath.replace(/\\/g, '/')).toMatch(/audioFiles\/fragment_AL1\.mp3$/);
  });

  it('files the mp4 of a TV note edited on its video, and the mp3 of one that fell back to audio', async () => {
    const { service } = build(null);

    present('audioFiles/fragment_AL1.mp4', 'audioFiles/fragment_AL1.mp3');
    await service.archiveNoteClip({ ...radioNote, media: 'tv' }, 'AL1');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body).filePath).toMatch(/fragment_AL1\.mp4$/);

    present('audioFiles/fragment_AL2.mp3');
    await service.archiveNoteClip({ ...radioNote, media: 'tv' }, 'AL2');
    expect(JSON.parse(fetchMock.mock.calls[1][1].body).filePath).toMatch(/fragment_AL2\.mp3$/);
  });

  it('leaves alone a note with no clip: internet, no file on disk, or too little to place it', async () => {
    const { service } = build(null);
    present('audioFiles/fragment_AL1.mp3');

    expect(await service.archiveNoteClip({ ...radioNote, media: 'internet' }, 'AL1')).toBe(false);
    expect(await service.archiveNoteClip({ ...radioNote, clientName: undefined }, 'AL1')).toBe(false);
    expect(await service.archiveNoteClip(radioNote, undefined)).toBe(false);
    present();
    expect(await service.archiveNoteClip(radioNote, 'AL1')).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('stores the public link of the clip in the note when it has none', async () => {
    present('audioFiles/fragment_AL1.mp3');
    fetchMock.mockResolvedValue(workerReply({ path: 'colombia/ecopetrol/2026/10/14/x.mp3', link: 'https://chrmonitoreo.org/colombia/ecopetrol/2026/10/14/x.mp3' }));
    const note = { alert_id: 'AL1' } as Record<string, unknown>;
    const { service, saved } = build(null, note);

    await service.archiveNoteClip(radioNote, 'AL1');

    expect(saved[0]).toMatchObject({ link: 'https://chrmonitoreo.org/colombia/ecopetrol/2026/10/14/x.mp3' });
  });

  it('keeps the link a note already has', async () => {
    present('audioFiles/fragment_AL1.mp3');
    fetchMock.mockResolvedValue(workerReply({ path: 'p', link: 'https://chrmonitoreo.org/x.mp3' }));
    const { service, saved } = build(null, { alert_id: 'AL1', link: 'https://news.example/own' });

    await service.archiveNoteClip(radioNote, 'AL1');

    expect(saved).toHaveLength(0);
  });

  it('never lets an archive failure break saving the note', async () => {
    present('audioFiles/fragment_AL1.mp3');
    fetchMock.mockResolvedValue(workerReply({ message: 'No se pudo subir el clip' }, false, 502));
    const { service } = build(null);
    const log = jest.spyOn(console, 'error').mockImplementation(() => undefined);

    await expect(service.archiveNoteClip(radioNote, 'AL1')).resolves.toBe(false);

    expect(log).toHaveBeenCalled();
    log.mockRestore();
  });
});

describe('NotesService.setNote and the clip', () => {
  it('starts filing the clip of a radio note once the note is saved, without waiting for it', async () => {
    const existing = { alert_id: 'AL1', title: 'T' };
    const { service } = build(null, existing);
    const archive = jest.spyOn(service, 'archiveNoteClip').mockResolvedValue(true);

    await service.setNote({
      id: ALERT_ID, title: 'Nuevo', date: '2026-10-14', media: 'radio', mediaName: 'Blu Radio', clientName: 'Ecopetrol',
      startTime: '2026-10-14T15:30:48.000Z', summary: 's', index: '10', program: 'p',
    });

    expect(archive).toHaveBeenCalledWith(expect.objectContaining({ media: 'radio', clientName: 'Ecopetrol' }), 'AL1');
  });
});

describe('NotesService.dailyReport', () => {
  const internetNote = {
    media: 'internet', clientName: 'Ecopetrol', date: '2026-10-23', mediaName: 'Infobae', title: 'Titular',
    summary: 'Resumen', subtopic: 'Carlos Suarez', attachment: 'https://chrdivulgar.org/x.pdf',
  };

  it('asks only for the internet notes of the client in the range and names the file after the last day', async () => {
    const clients = { getClients: jest.fn().mockResolvedValue([{ name: 'Ecopetrol', reportIndicators: [{ source: 'El Tiempo', label: 'Divisas' }] }]) };
    const { service } = build(null, internetNote, clients);

    const report = await service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23');

    const query = (service.noteRepo.find as jest.Mock).mock.calls[0][0];
    expect(query.where).toEqual({ media: 'internet', clientName: 'Ecopetrol', date: { $gte: '2026-10-22', $lte: '2026-10-23' } });
    expect(report.fileName).toBe('Informe Ecopetrol-23-10-26.docx');
    expect(report.content.subarray(0, 2).toString()).toBe('PK'); // a .docx is a zip
  });

  it('says so when there is no note to report, and refuses a missing client or a reversed range', async () => {
    const { service } = build(null, null, { getClients: jest.fn().mockResolvedValue([]) });

    await expect(service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23')).rejects.toThrow('No hay notas de internet de Ecopetrol');
    await expect(service.dailyReport('', '2026-10-22', '2026-10-23')).rejects.toThrow('Elige el cliente');
    await expect(service.dailyReport('Ecopetrol', '2026-10-24', '2026-10-23')).rejects.toThrow('Elige el cliente');
  });
});
