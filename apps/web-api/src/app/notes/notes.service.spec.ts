import * as fs from 'fs';
import { NotesService } from './notes.service';

// Minimal in-memory stand-ins for the two Mongo repositories the service reads and writes.
function build(
  alert: Record<string, unknown> | null,
  existingNote: Record<string, unknown> | Record<string, unknown>[] | null = null,
  clients: unknown = {},
) {
  const existingNotes = Array.isArray(existingNote)
    ? existingNote
    : existingNote
      ? [existingNote]
      : [];
  const saved: Record<string, unknown>[] = [];
  const noteRepo = {
    delete: jest.fn().mockResolvedValue({ affected: 1 }),
    find: jest.fn().mockResolvedValue(existingNotes),
    findOne: jest.fn().mockResolvedValue(existingNotes[0] ?? null),
    findOneBy: jest.fn().mockResolvedValue(existingNotes[0] ?? null),
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
    audioLabel: 'NOTA_01_BLU_RADIO_14-10-26_ECOP.mp3',
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
      audioLabel: 'NOTA_01_BLU_RADIO_14-10-26_ECOP.mp3',
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

describe('NotesService.bulkUpdateSentiment', () => {
  it('updates sentiment of valid note ids and reports missing/invalid ids', async () => {
    const firstId = '507f1f77bcf86cd799439011';
    const secondId = '507f1f77bcf86cd799439012';
    const missingId = '507f1f77bcf86cd799439013';
    const first = { _id: { toString: () => firstId }, sentiment: 'Neutra' } as unknown as Record<string, unknown>;
    const second = { _id: { toString: () => secondId }, sentiment: 'Negativa' } as unknown as Record<string, unknown>;
    const { service } = build(null, [first, second]);
    const findOneByMock = jest
      .spyOn(service.noteRepo, 'findOneBy')
      .mockImplementation(async (where: unknown) => {
        const id = (where as { _id?: { toString: () => string } })?._id?.toString();
        if (id === firstId) return first as never;
        if (id === secondId) return second as never;
        return null;
      });

    const result = await service.bulkUpdateSentiment({
      noteIds: [firstId, secondId, missingId, 'invalid-id'],
      sentiment: 'Positiva',
    });

    expect(result).toEqual({
      updatedCount: 2,
      notFoundIds: [missingId, 'invalid-id'],
    });
    expect((first as { sentiment: string }).sentiment).toBe('Positiva');
    expect((second as { sentiment: string }).sentiment).toBe('Positiva');
    expect(findOneByMock).toHaveBeenCalled();
  });

  it('rejects an empty list and invalid sentiment', async () => {
    const { service } = build(null);

    await expect(
      service.bulkUpdateSentiment({ noteIds: [], sentiment: 'Positiva' }),
    ).rejects.toThrow('Selecciona al menos una nota');

    await expect(
      service.bulkUpdateSentiment({
        noteIds: ['507f1f77bcf86cd799439011'],
        sentiment: 'Muy positiva',
      }),
    ).rejects.toThrow('Sentimiento inválido');
  });
});

describe('NotesService.dailyReport', () => {
  const internetNote = {
    media: 'internet', clientName: 'Ecopetrol', date: '2026-10-23', mediaName: 'Infobae', title: 'Titular',
    summary: 'Resumen', subtopic: 'Carlos Suarez', attachment: 'https://chrdivulgar.org/x.pdf',
  };

  it('asks for the selected media of the client in the range and names the file after the last day', async () => {
    const clients = { getClients: jest.fn().mockResolvedValue([{ name: 'Ecopetrol', reportIndicators: [{ source: 'El Tiempo', label: 'Divisas' }] }]) };
    const { service } = build(null, internetNote, clients);

    const report = await service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23', ['internet']);

    const query = (service.noteRepo.find as jest.Mock).mock.calls[0][0];
    expect(query.where).toEqual({ media: { $in: ['internet'] }, clientName: 'Ecopetrol', date: { $gte: '2026-10-22', $lte: '2026-10-23' } });
    expect(report.fileName).toBe('Informe Ecopetrol-23-10-26.docx');
    expect(report.content.subarray(0, 2).toString()).toBe('PK'); // a .docx is a zip
  });

  it('expands television aliases when tv is selected', async () => {
    const clients = { getClients: jest.fn().mockResolvedValue([{ name: 'Ecopetrol' }]) };
    const { service } = build(null, { ...internetNote, media: 'tv' }, clients);

    await service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23', ['tv', 'radio']);

    const query = (service.noteRepo.find as jest.Mock).mock.calls[0][0];
    expect(query.where.media.$in).toEqual(expect.arrayContaining(['tv', 'television', 'televisión', 'radio']));
  });

  it('says so when there is no note to report, and refuses missing media, a missing client or a reversed range', async () => {
    const { service } = build(null, null, { getClients: jest.fn().mockResolvedValue([]) });

    await expect(service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23', ['internet'])).rejects.toThrow('No hay notas de internet de Ecopetrol');
    await expect(service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23', [])).rejects.toThrow('Elige al menos un medio');
    await expect(service.dailyReport('Ecopetrol', '2026-10-22', '2026-10-23', ['prensa'])).rejects.toThrow('solo permite Radio, Televisión e Internet');
    await expect(service.dailyReport('', '2026-10-22', '2026-10-23', ['internet'])).rejects.toThrow('Elige el cliente');
    await expect(service.dailyReport('Ecopetrol', '2026-10-24', '2026-10-23', ['internet'])).rejects.toThrow('Elige el cliente');
  });
});

describe('NotesService.deleteNote', () => {
  const fetchMock = jest.fn();
  beforeEach(() => {
    fetchMock.mockReset();
    global.fetch = fetchMock as never;
  });

  const webNote = {
    id: ALERT_ID,
    media: 'internet',
    attachment: 'https://chrdivulgar.org/colombia/ecopetrol/2026/10/23/infobae-01-x.pdf',
    link: 'https://www.infobae.com/nota',
  };

  it('deletes the archived file of the note through the worker, then the note', async () => {
    fetchMock.mockResolvedValue(workerReply({ deleted: ['colombia/ecopetrol/2026/10/23/infobae-01-x.pdf'], failed: [] }));
    const { service } = build(null, webNote);

    await service.deleteNote(ALERT_ID);

    expect(fetchMock.mock.calls[0][0]).toMatch(/\/notes\/archive\/delete$/);
    // Every link of the note goes: the worker only deletes the ones that are the note's own file in the archive.
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ links: [webNote.attachment, webNote.link] });
    expect(service.noteRepo.delete).toHaveBeenCalledWith(ALERT_ID);
  });

  it('keeps the note when its file could not be deleted from the archive, so it can be tried again', async () => {
    fetchMock.mockResolvedValue(workerReply({ deleted: [], failed: [webNote.attachment] }));
    const { service } = build(null, webNote);

    await expect(service.deleteNote(ALERT_ID)).rejects.toThrow('servidor de archivo');
    expect(service.noteRepo.delete).not.toHaveBeenCalled();
  });

  it('deletes a note that has nothing archived without asking the worker', async () => {
    const { service } = build(null, { id: ALERT_ID, media: 'prensa' });

    await service.deleteNote(ALERT_ID);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(service.noteRepo.delete).toHaveBeenCalledWith(ALERT_ID);
  });

  it('says so when the note does not exist', async () => {
    const { service } = build(null, null);

    await expect(service.deleteNote(ALERT_ID)).rejects.toThrow('no existe');
  });
});

describe('NotesService.importNotes (Excel)', () => {
  // The repository the import uses: notes already stored, and what insertMany receives.
  function buildImport(stored: Record<string, unknown>[] = []) {
    const inserted: Record<string, unknown>[] = [];
    const noteRepo = {
      find: jest.fn().mockResolvedValue(stored),
      create: jest.fn((fields: Record<string, unknown>) => ({ ...fields })),
      insertMany: jest.fn(async (notes: Record<string, unknown>[]) => {
        inserted.push(...notes);
        return notes;
      }),
    };
    const dataSource = { getMongoRepository: () => noteRepo };
    const service = new NotesService(dataSource as never, {} as never, {} as never);
    return { service, inserted, noteRepo };
  }

  const row = (fields: Record<string, unknown> = {}) => ({
    sheet: 'Oct 26 a nov 01',
    row: 2,
    date: '2026-10-26',
    media: 'Internet',
    mediaName: 'El Frente',
    title: 'Cayó alias ‘Visajes’, señalado de amenazar a comerciantes',
    ...fields,
  });

  it('imports a valid row under the chosen client', async () => {
    const { service, inserted } = buildImport();

    const result = await service.importNotes({ clientName: 'Ecopetrol', notes: [row()] });

    expect(result).toEqual({ inserted: 1, duplicates: 0, errors: [] });
    expect(inserted[0]).toMatchObject({ clientName: 'Ecopetrol', title: row().title, media: 'Internet' });
    expect(inserted[0]).not.toHaveProperty('sheet');
  });

  it('reports each row it cannot take, with its sheet, row and reason, instead of skipping it silently', async () => {
    const { service, inserted } = buildImport();

    const result = await service.importNotes({
      clientName: 'Ecopetrol',
      notes: [row({ row: 5, title: '' }), row({ row: 6, date: '46320' }), row({ row: 7, media: '', mediaName: '' })],
    });

    expect(result.inserted).toBe(0);
    expect(inserted).toHaveLength(0);
    expect(result.errors).toEqual([
      { sheet: 'Oct 26 a nov 01', row: 5, reason: 'Falta: Titular' },
      { sheet: 'Oct 26 a nov 01', row: 6, reason: 'Fecha inválida: "46320"' },
      { sheet: 'Oct 26 a nov 01', row: 7, reason: 'Falta: Tipo de medio, Medio' },
    ]);
  });

  it('keeps the portal note when the Excel brings it with other capitals, accents or quotes', async () => {
    const portalNote = {
      _id: 'p1',
      date: '2026-10-26',
      media: 'internet',
      mediaName: 'El Frente',
      title: "Cayo alias 'Visajes', señalado de amenazar a comerciantes",
      attachment: 'https://chrdivulgar.org/x.pdf',
    };
    const { service, inserted, noteRepo } = buildImport([portalNote]);

    const result = await service.importNotes({ clientName: 'Ecopetrol', notes: [row(), row({ media: 'Televisión', mediaName: 'RCN', title: 'Otra' })] });

    expect(result).toEqual({ inserted: 1, duplicates: 1, errors: [] });
    expect(inserted.map((note) => note.title)).toEqual(['Otra']);
    expect(portalNote.attachment).toBe('https://chrdivulgar.org/x.pdf'); // untouched
    // One query for the dates of the file, not one per row.
    expect(noteRepo.find).toHaveBeenCalledTimes(1);
  });

  it('treats televisión and tv as the same medium', async () => {
    const { service } = buildImport([{ date: '2026-10-26', media: 'tv', mediaName: 'RCN', title: 'Nota' }]);

    const result = await service.importNotes({ clientName: 'Ecopetrol', notes: [row({ media: 'Televisión', mediaName: 'RCN', title: 'Nota' })] });

    expect(result.duplicates).toBe(1);
  });

  it('recognizes the portal note under the outlet name the Excel uses', async () => {
    const { service, inserted } = buildImport([
      { date: '2026-10-28', media: 'internet', mediaName: 'LaRepublica.co', title: 'Ecopetrol revelará sus resultados' },
      { date: '2026-10-28', media: 'internet', mediaName: 'Revista Semana', title: 'ISA eligió nueva junta' },
      { date: '2026-10-26', media: 'internet', mediaName: 'Las2orillas', title: 'La empresa de Roa' },
      { date: '2026-10-28', media: 'internet', mediaName: 'Forbes Colombia', title: 'Asamblea de ISA cambia reglas' },
    ]);

    const result = await service.importNotes({
      clientName: 'Ecopetrol',
      notes: [
        row({ date: '2026-10-28', mediaName: 'La República', title: 'Ecopetrol revelará sus resultados' }),
        row({ date: '2026-10-28', mediaName: 'Semana', title: 'ISA eligió nueva junta' }),
        row({ date: '2026-10-26', mediaName: 'Las 2 Orillas', title: 'La empresa de Roa' }),
        row({ date: '2026-10-28', mediaName: 'Forbes', title: 'Asamblea de ISA cambia reglas' }),
      ],
    });

    expect(result).toMatchObject({ inserted: 0, duplicates: 4 });
    expect(inserted).toHaveLength(0);
  });

  it('keeps notes that only share a link: social posts and press PDFs carry one address for several stories', async () => {
    const { service, inserted } = buildImport();

    await service.importNotes({
      clientName: 'Ecopetrol',
      notes: [
        row({ media: 'Redes Sociales', mediaName: 'Facebook', title: 'Uno', link: 'https://facebook.com/story.php?id=1' }),
        row({ media: 'Redes Sociales', mediaName: 'Facebook', title: 'Dos', link: 'https://facebook.com/story.php?id=2' }),
      ],
    });

    expect(inserted).toHaveLength(2);
  });

  it('keeps the same headline from two different outlets: a syndicated story is two notes', async () => {
    const { service, inserted } = buildImport();

    const result = await service.importNotes({
      clientName: 'Ecopetrol',
      notes: [
        row({ mediaName: 'La FM', title: 'Nuevo nombramiento en Ecopetrol' }),
        row({ mediaName: 'RCN Noticias', title: 'Nuevo nombramiento en Ecopetrol' }),
        // "el norte" is inside "diariodelnorte" as letters, not as words: two outlets.
        row({ mediaName: 'Diario del Norte', title: 'Nuevo nombramiento en Ecopetrol' }),
        row({ mediaName: 'El Norte', title: 'Nuevo nombramiento en Ecopetrol' }),
      ],
    });

    expect(result.inserted).toBe(4);
    expect(inserted).toHaveLength(4);
  });

  it('saves a note repeated inside the same file only once', async () => {
    const { service, inserted } = buildImport();

    const result = await service.importNotes({ clientName: 'Ecopetrol', notes: [row(), row({ row: 9, title: row().title.toUpperCase() })] });

    expect(result).toMatchObject({ inserted: 1, duplicates: 1 });
    expect(inserted).toHaveLength(1);
  });

  it('refuses an import without a client', async () => {
    const { service } = buildImport();

    await expect(service.importNotes({ clientName: '  ', notes: [row()] })).rejects.toThrow('cliente');
  });

  it('refuses a body that is not a list of notes', async () => {
    const { service } = buildImport();

    await expect(service.importNotes({ clientName: 'Ecopetrol', notes: 'x' as never })).rejects.toThrow();
  });
});

describe('NotesService.generateMessage', () => {
  it('formats the note start time in Bogotá time (UTC-5)', () => {
    const { service } = build(null, null);

    const message = service.generateMessage({
      index: '1',
      program: 'Blu Radio',
      title: 'Titular',
      summary: 'Resumen',
      startTime: '2026-11-25T12:40:00.000Z',
    });

    expect(message).toContain('(7:40 AM)');
  });

  it('keeps duration text and Bogotá time together', () => {
    const { service } = build(null, null);

    const message = service.generateMessage({
      index: '2',
      program: 'LaFM',
      title: 'Otra nota',
      summary: 'Resumen largo',
      startTime: '2026-11-25T13:05:10.000Z',
      duration: 90,
    });

    expect(message).toContain('(1’30”)');
    expect(message).toContain('(8:05 AM)');
  });
});

describe('NotesService.sendMessage: the alert learns its note was sent', () => {
  const noteDto = { alert_id: 'al1', clientName: 'Ecopetrol', media: 'radio', message: 'NOTA 1 ...' };

  function buildSender(send: jest.Mock) {
    const note = { id: 'n1', alert_id: 'al1', title: 'Titular' } as Record<string, unknown>;
    const noteRepo = {
      findOne: jest.fn().mockResolvedValue(note),
      save: jest.fn(async (saved: Record<string, unknown>) => saved),
    };
    const dataSource = { getMongoRepository: () => noteRepo };
    const clients = { getClients: jest.fn().mockResolvedValue([{ name: 'Ecopetrol', notes: { radio: ['+57300', '+57301'] } }]) };
    const service = new NotesService(dataSource as never, clients as never, { send } as never);
    return { service, note, noteRepo };
  }

  it('saves when it was sent', async () => {
    const { service, note } = buildSender(jest.fn().mockResolvedValue(undefined));

    await expect(service.sendMessage(noteDto as never)).resolves.toBe(true);

    expect(note.sentAt).toBeInstanceOf(Date);
  });

  it('saves it too when only some recipients got it: the note did reach someone', async () => {
    const send = jest.fn().mockImplementation(async (recipient: string) => {
      if (recipient === '+57301') throw new Error('sin sesión');
    });
    const { service, note } = buildSender(send);

    await expect(service.sendMessage(noteDto as never)).rejects.toThrow(/1 de 2/);

    expect(note.sentAt).toBeInstanceOf(Date);
  });

  it('does not mark it as sent when nobody got it', async () => {
    const { service, note } = buildSender(jest.fn().mockRejectedValue(new Error('sin sesión')));

    await expect(service.sendMessage(noteDto as never)).rejects.toThrow(/No se pudo enviar/);

    expect(note.sentAt).toBeUndefined();
  });
});
