import { BadRequestException, Injectable } from '@nestjs/common';
import { DataSource, MongoRepository } from 'typeorm';
import { ObjectId } from 'mongodb';
import {
  ImportNotesRequestDto,
  ImportNotesResultDto,
  NoteDto,
  InternetNotePreviewDto,
  SaveInternetNoteDto,
  DashboardDataDto,
  DashboardPeriod,
  NoteOrigin,
  NoteSentiment,
  TableByMediaNameItem,
  MediaGroupItem,
  MediaNameSentimentItem,
} from '@repo/shared';
import { Note, WorkerAlert } from '../entities';
import { InjectDataSource } from '@nestjs/typeorm';
import * as moment from 'moment';
import * as path from 'path';
import { ClientsService } from '../clients/clients.service';
import { WhatsAppService } from '../settings/whatsapp.service';
import { noteMediaKind } from '../video/video.helpers';
import { buildDailyReport, dailyReportFileName } from './daily-report';
import * as fs from 'fs';

// Lowercase, no accents, any run of other characters (quotes, dashes...) as one space.
const foldText = (text?: unknown): string =>
  String(text ?? '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();

// An outlet's name in words, without a closing domain: "LaRepublica.co" is "larepublica".
const outletWords = (name?: unknown): string => foldText(name).replace(/ (com co|com|co|net|org)$/, '');

// The portal names outlets as Google News does ("LaRepublica.co", "Revista Semana", "Forbes Colombia") and the Excel
// as people do ("La República", "Semana", "Forbes"). The same outlet: equal without spaces, or one name inside the other
// in whole words ("Diario del Norte" is not "El Norte").
const sameOutlet = (a?: unknown, b?: unknown): boolean => {
  const [x, y] = [outletWords(a), outletWords(b)];
  if (!x || !y) return false;
  if (x.replace(/ /g, '') === y.replace(/ /g, '')) return true;
  const [short, long] = x.length <= y.length ? [x, y] : [y, x];
  return ` ${long} `.includes(` ${short} `);
};

@Injectable()
export class NotesService {
  // Hours before the article's own PDF copy gives up: the worker prints, then uploads over FTPS.
  private static readonly PDF_TIMEOUT_MS = 120_000;
  // Same day boundary the alerts UI uses for worker alerts (see AlertsService.getAlerts).
  private static readonly BOGOTA_OFFSET_MINUTES = -5 * 60;

  noteRepo: MongoRepository<Note>;
  workerAlertRepo: MongoRepository<WorkerAlert>;

  constructor(
    @InjectDataSource('monitoring') private readonly dataSource: DataSource,
    private readonly clientsService: ClientsService,
    private readonly whatsAppService: WhatsAppService,
  ) {
    this.noteRepo = this.dataSource.getMongoRepository(Note);
    this.workerAlertRepo = this.dataSource.getMongoRepository(WorkerAlert);
  }

  /**
   * Opens an internet alert for editing (RQ-014): the worker prints the article's page to a scratch
   * PDF and says which public link it will have. Nothing is archived and no note is written yet;
   * that happens in saveInternetNote. The draft carries what the user already edited on an
   * earlier pass, else what the alert knows.
   */
  async previewInternetNote(alertId: string): Promise<InternetNotePreviewDto> {
    const alert = await this.loadInternetAlert(alertId);
    const title = alert.title?.trim() || `nota-${alertId}`;
    const published = this.publishedAt(alert);

    const preview = await this.callWorker<{ previewId: string; link: string }>(
      '/notes/pdf/preview',
      {
        outlet: alert.platform,
        title,
        url: alert.url,
        date: published.format(),
        // The worker files the PDF under the day of the 6 a.m. report: {year}/{MONTH}/{day}.
        client: alert.clientName,
        // Marked in yellow in the PDF, as when searching the page in a browser.
        keywords: await this.highlightTerms(alert),
        alertId,
      },
    );

    return this.previewDraft(alert, alertId, title, preview);
  }

  /**
   * The words to highlight in the PDF: the ones the alert matched. An alert left for manual review (the page
   * could not be read) has none, so the client's own keywords are used instead. Never fails the preview.
   */
  private async highlightTerms(alert: WorkerAlert): Promise<string[]> {
    const matched = (alert.words ?? []).filter((word) => word?.trim());
    if (matched.length > 0) {
      return matched;
    }
    try {
      const clients = await this.clientsService.getClients();
      const client = clients.find((candidate) => candidate.name === alert.clientName);
      return (client?.words ?? [])
        .map((word) => word.value?.trim())
        .filter((value): value is string => !!value);
    } catch (error) {
      console.error('Could not read the client keywords to highlight the PDF:', error);
      return [];
    }
  }

  /**
   * The same preview from a PDF the user made themselves (the page could not be printed or verified, so the alert
   * is left for manual review). The worker keeps the file as the scratch preview and says which link it will have;
   * saving the note then archives it exactly like a printed one.
   */
  async uploadInternetNotePdf(
    alertId: string,
    pdf: Buffer,
  ): Promise<InternetNotePreviewDto> {
    if (!pdf?.length) {
      throw new Error('No se recibió ningún PDF.');
    }
    const alert = await this.loadInternetAlert(alertId);
    const title = alert.title?.trim() || `nota-${alertId}`;

    const query = new URLSearchParams({
      outlet: alert.platform ?? '',
      title,
      date: this.publishedAt(alert).format(),
      client: alert.clientName ?? '',
    });
    const workerUrl =
      process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const response = await fetch(`${workerUrl}/notes/pdf/upload?${query}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/pdf' },
      body: pdf as unknown as BodyInit, // Node sends a Buffer as is; the DOM typings do not list it
      signal: AbortSignal.timeout(NotesService.PDF_TIMEOUT_MS),
    });
    const parsed = (await response.json().catch(() => ({}))) as {
      previewId: string;
      link: string;
      message?: string;
    };
    if (!response.ok) {
      throw new Error(
        parsed.message ?? `El servicio de PDF respondió ${response.status}`,
      );
    }
    return this.previewDraft(alert, alertId, title, parsed);
  }

  // What the editor opens with: the PDF's preview and the note as the user left it on an earlier pass, else as the
  // alert knows it.
  private async previewDraft(
    alert: WorkerAlert,
    alertId: string,
    title: string,
    preview: { previewId: string; link: string },
  ): Promise<InternetNotePreviewDto> {
    const existing = await this.noteRepo.findOne({
      where: { alert_id: alertId },
    });
    return {
      previewId: preview.previewId,
      pdfLink: preview.link,
      note: {
        id: existing?.id,
        index: existing?.index,
        program: existing?.program || alert.platform,
        title: existing?.title || title,
        summary: existing?.summary || alert.text,
        link: alert.url,
        topic: existing?.topic,
        subtopic: existing?.subtopic,
        subsubtopic: existing?.subsubtopic,
        origin: existing?.origin,
        sentiment: existing?.sentiment,
        zone: existing?.zone,
        city: existing?.city,
        rate: existing?.rate,
        audience: existing?.audience,
        value: existing?.value,
      },
    };
  }

  /** The previewed PDF itself, for the editor's viewer. */
  async getInternetPreviewPdf(previewId: string): Promise<Buffer> {
    const workerUrl =
      process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const response = await fetch(
      `${workerUrl}/notes/pdf/preview?id=${encodeURIComponent(previewId)}`,
      { signal: AbortSignal.timeout(NotesService.PDF_TIMEOUT_MS) },
    );
    if (!response.ok) {
      const body = (await response.json().catch(() => ({}))) as {
        message?: string;
      };
      throw new Error(
        body.message ?? `El servicio de PDF respondió ${response.status}`,
      );
    }
    return Buffer.from(await response.arrayBuffer());
  }

  /**
   * Saves the internet note: only now does the previewed PDF go to the archive, and its final link
   * is stored in `attachment` (read-only in the UI). Saving again for the same alert updates the
   * same note and overwrites the same PDF.
   */
  async saveInternetNote(dto: SaveInternetNoteDto): Promise<NoteDto> {
    if (!dto.title?.trim()) {
      throw new Error('Falta el titular de la nota.');
    }
    const alert = await this.loadInternetAlert(dto.alertId);
    const published = this.publishedAt(alert);

    const { link } = await this.callWorker<{ link: string }>(
      '/notes/pdf/commit',
      { previewId: dto.previewId },
    );

    let note = await this.noteRepo.findOne({
      where: { alert_id: dto.alertId },
    });
    note ??= this.noteRepo.create({ alert_id: dto.alertId });

    note.media = 'internet';
    note.mediaName = alert.platform;
    note.clientName = alert.clientName;
    note.date = published.format('YYYY-MM-DD');
    note.startTime = published.format();
    note.link = alert.url;
    note.attachment = link;
    note.index = dto.index ?? note.index;
    note.program = dto.program ?? note.program;
    note.title = dto.title.trim();
    note.summary = dto.summary ?? alert.text;
    // The classification and pricing the user chose; whatever they left empty stays as it was.
    note.topic = dto.topic ?? note.topic;
    note.subtopic = dto.subtopic ?? note.subtopic;
    note.subsubtopic = dto.subsubtopic ?? note.subsubtopic;
    note.origin = dto.origin ?? note.origin;
    note.sentiment = dto.sentiment ?? note.sentiment;
    note.zone = dto.zone ?? note.zone;
    note.city = dto.city ?? note.city;
    note.rate = dto.rate ?? note.rate;
    note.audience = dto.audience ?? note.audience;
    note.value = dto.value ?? note.value;
    note.text = note.text || alert.text;
    note.message = this.generateMessage(note);

    return await this.noteRepo.save(note);
  }

  private async loadInternetAlert(alertId: string): Promise<WorkerAlert> {
    const alert = await this.workerAlertRepo.findOneBy({
      _id: new ObjectId(alertId),
    } as unknown as Partial<WorkerAlert>);
    if (!alert || alert.media !== 'internet' || !alert.url) {
      throw new Error('La alerta no existe o no es una nota de internet.');
    }
    return alert;
  }

  private publishedAt(alert: WorkerAlert): moment.Moment {
    return moment(alert.startTime).utcOffset(NotesService.BOGOTA_OFFSET_MINUTES);
  }

  private async callWorker<T>(route: string, body: unknown): Promise<T> {
    const workerUrl =
      process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const response = await fetch(`${workerUrl}${route}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(NotesService.PDF_TIMEOUT_MS),
    });

    const parsed = (await response.json().catch(() => ({}))) as T & {
      message?: string;
    };
    if (!response.ok) {
      throw new Error(
        parsed.message ?? `El servicio de PDF respondió ${response.status}`,
      );
    }
    return parsed;
  }

  /**
   * Fills the date, medium, outlet and client a note is missing from the alert it came from, so an
   * editor that only knows the clip (not the alert's own data) still saves a complete note. What
   * the note already has is never overwritten. The date is the Bogotá day of the clip when there
   * is one, else of the alert.
   */
  private async completeFromAlert(note: NoteDto, alertId?: string): Promise<NoteDto> {
    const missing = !note.date || !note.media || !note.mediaName || !note.clientName;
    if (!missing || !alertId || !ObjectId.isValid(alertId)) {
      return note;
    }

    const alert = await this.workerAlertRepo.findOneBy({
      _id: new ObjectId(alertId),
    } as unknown as Partial<WorkerAlert>);
    if (!alert) {
      return note;
    }

    const when = moment(note.startTime ?? alert.startTime).utcOffset(
      NotesService.BOGOTA_OFFSET_MINUTES,
    );
    return {
      ...note,
      date: note.date || when.format('YYYY-MM-DD'),
      media: note.media || alert.media,
      mediaName: note.mediaName || alert.platform,
      clientName: note.clientName || alert.clientName,
    };
  }

  async setNote(input: NoteDto): Promise<NoteDto> {
    const existingNote = await this.noteRepo.findOneBy({
      _id: new ObjectId(input.id),
    });

    if (!existingNote) {
      throw new Error('Note not found');
    }

    // A note made from an alert already has its date, medium, outlet and client in that alert.
    const noteDto = await this.completeFromAlert(input, existingNote.alert_id);

    // Validar campos obligatorios
    if (
      !noteDto.date ||
      !noteDto.media ||
      !noteDto.mediaName ||
      !noteDto.title
    ) {
      throw new Error(
        'Faltan campos obligatorios: fecha, medio, nombre del medio o título',
      );
    }

    // A generated PDF link is read-only: it can only change by generating the PDF again.
    const { attachment, ...editable } = noteDto;
    const keepGeneratedLink = /^https?:\/\//i.test(existingNote.attachment ?? '');
    const incoming = keepGeneratedLink ? editable : { ...editable, attachment };
    incoming.message = this.generateMessage({ ...existingNote, ...incoming });
    Object.assign(existingNote, incoming);
    // Save the updated note
    const saved = await this.noteRepo.save(existingNote);

    // The clip of a radio or TV note is filed in the archive too. It is a copy, so saving the note never waits for
    // it (a TV clip is tens of MB) and never fails because of it.
    void this.archiveNoteClip(noteDto, existingNote.alert_id);
    return saved;
  }

  // The public link of the clip is the note's "enlace o archivo" (RQ-021) — unless the note already has a link of its own.
  private async keepClipLink(alertId: string, link?: string): Promise<void> {
    if (!link) return;
    const note = await this.noteRepo.findOne({ where: { alert_id: alertId } });
    if (note && !note.link) {
      note.link = link;
      await this.noteRepo.save(note);
    }
  }

  /**
   * Files the clip of a radio or TV note (the mp3, or the mp4 of a TV note edited on its video) in the archive
   * account of its medium, under {country}/{client}/{year}/{month}/{day}, through the worker. These are the notes,
   * so they are kept for the whole contract. Returns whether it was filed; a failure is logged, never thrown.
   */
  async archiveNoteClip(note: NoteDto, alertId?: string): Promise<boolean> {
    const media = (note.media ?? '').trim().toLowerCase();
    if (
      !alertId ||
      !['radio', 'tv', 'television'].includes(media) ||
      !note.clientName ||
      !note.mediaName ||
      !note.date
    ) {
      return false;
    }

    const clip = (extension: string) =>
      path.resolve(`./audioFiles/fragment_${alertId}.${extension}`);
    const filePath =
      noteMediaKind(media, fs.existsSync(clip('mp4'))) === 'video' ? clip('mp4') : clip('mp3');
    if (!fs.existsSync(filePath)) {
      return false;
    }

    const moment_ = moment(note.startTime ?? note.date).utcOffset(
      NotesService.BOGOTA_OFFSET_MINUTES,
    );
    try {
      const filed = await this.callWorker<{ path: string; link?: string }>('/notes/clip', {
        medium: media,
        client: note.clientName,
        platform: note.mediaName,
        date: note.date,
        stamp: moment_.format('YYYY-MM-DD_HH-mm-ss'),
        alertId,
        filePath,
      });
      await this.keepClipLink(alertId, filed.link);
      return true;
    } catch (error) {
      console.error(`Could not file the clip of note ${alertId} in the archive:`, error);
      return false;
    }
  }

  generateMessage(note: NoteDto): string {
    function formatDuration(seconds: number): string {
      const hours = Math.floor(seconds / 3600);
      const minutes = Math.floor((seconds % 3600) / 60);
      const remainingSeconds = seconds % 60;

      let formattedDuration = '';

      if (hours > 0) {
        formattedDuration += `${hours}h `;
      }

      if (minutes > 0) {
        formattedDuration += `${minutes}’`;
      }

      formattedDuration += `${remainingSeconds}”`;

      return `(${formattedDuration.trim()})`;
    }

    function formatPlainText(text: string): string {
      return text.replace(/[\n\r\t]/g, ' ').trim();
    }

    // Web notes have no clip, so no duration to show.
    const duration =
      typeof note.duration === 'number' ? ` ${formatDuration(note.duration)}` : '';
    return `NOTA ${note.index} ${note.program} **${note.title}** ${formatPlainText(note.summary ?? '')}${duration} (${moment(note.startTime).format('h:mm A')})`;
  }

  /**
   * Sends the note (text, then its clip) to every recipient configured for the note's client and
   * media in `clients.notes[media]` — phone numbers or WhatsApp group JIDs — through the worker's
   * WhatsApp session. The clip is the MP4 for a TV note edited on its video, the mp3 otherwise.
   * Throws if nobody could be reached.
   */
  async sendMessage(noteDto: NoteDto): Promise<boolean> {
    const recipients = await this.resolveNoteRecipients(noteDto);

    const audioFilePath = path.resolve(
      `./audioFiles/fragment_${noteDto.alert_id}.mp3`,
    );
    const videoFilePath = path.resolve(
      `./audioFiles/fragment_${noteDto.alert_id}.mp4`,
    );
    const kind = noteMediaKind(noteDto.media, fs.existsSync(videoFilePath));

    const failures: string[] = [];
    for (const recipient of recipients) {
      try {
        await this.whatsAppService.send(recipient, noteDto.message);
        if (kind === 'video') {
          await this.whatsAppService.send(recipient, undefined, undefined, videoFilePath);
        } else {
          await this.whatsAppService.send(recipient, undefined, audioFilePath);
        }
      } catch (error) {
        failures.push(
          `${recipient}: ${error instanceof Error ? error.message : JSON.stringify(error)}`,
        );
      }
    }

    if (failures.length === recipients.length) {
      throw new Error(`No se pudo enviar la nota: ${failures.join('; ')}`);
    }
    if (failures.length > 0) {
      throw new Error(
        `Nota enviada a ${recipients.length - failures.length} de ${recipients.length} destinatarios; falló: ${failures.join('; ')}`,
      );
    }

    return true;
  }

  private async resolveNoteRecipients(noteDto: NoteDto): Promise<string[]> {
    const clientName = (noteDto.clientName ?? '').trim();
    const media = this.normalizeMediaKey(noteDto.media);
    if (!clientName || !media) {
      throw new Error('La nota necesita cliente y medio para saber a quién enviarla');
    }

    const clients = await this.clientsService.getClients();
    const client = clients.find(
      (c) => c.name.trim().toLowerCase() === clientName.toLowerCase(),
    );
    const notesByMedia = client?.notes ?? {};
    const key = Object.keys(notesByMedia).find(
      (k) => this.normalizeMediaKey(k) === media,
    );
    const recipients = (key ? notesByMedia[key] : []).filter((r) => r?.trim());

    if (recipients.length === 0) {
      throw new Error(
        `El cliente "${clientName}" no tiene destinatarios de notas configurados para ${media}`,
      );
    }
    return recipients;
  }

  // Media keys in config and in notes come as "tv" / "television" / "TV"; compare them uniformly.
  private normalizeMediaKey(media?: string): string {
    const key = (media ?? '').trim().toLowerCase();
    return key === 'television' || key === 'televisión' ? 'tv' : key;
  }

  /**
   * Notes from the monthly Excel, all for the client chosen on upload. A row that cannot be taken is
   * reported with its sheet, row and reason (never skipped silently), and a note that already exists
   * is left as it is: the one saved from the portal keeps its PDF. "The same note" is the same date,
   * medium kind, outlet and headline once capitals, accents, quotes and spacing are set aside
   * ("Internet" is "internet", "Televisión" is "tv").
   */
  async importNotes(body: ImportNotesRequestDto): Promise<ImportNotesResultDto> {
    const clientName = body?.clientName?.trim();
    if (!clientName) {
      throw new BadRequestException('Elige el cliente de las notas que vas a cargar.');
    }
    if (!Array.isArray(body.notes)) {
      throw new BadRequestException('La carga debe traer la lista de notas.');
    }

    const errors: ImportNotesResultDto['errors'] = [];
    const valid: ImportNotesRequestDto['notes'] = [];
    for (const note of body.notes) {
      const missing = (
        [
          [note.date, 'Fecha'],
          [note.media, 'Tipo de medio'],
          [note.mediaName, 'Medio'],
          [note.title, 'Titular'],
        ] as const
      )
        .filter(([value]) => !String(value ?? '').trim())
        .map(([, label]) => label);
      if (missing.length > 0) {
        errors.push({ sheet: note.sheet, row: note.row, reason: `Falta: ${missing.join(', ')}` });
      } else if (!moment(note.date, 'YYYY-MM-DD', true).isValid()) {
        errors.push({ sheet: note.sheet, row: note.row, reason: `Fecha inválida: "${note.date}"` });
      } else {
        valid.push(note);
      }
    }

    // The notes already stored for the file's dates, in one query.
    const dates = valid.map((note) => note.date as string).sort();
    const stored = dates.length
      ? await this.noteRepo.find({
          where: { date: { $gte: dates[0], $lte: dates[dates.length - 1] } },
        } as never)
      : [];
    // The same note is the same story (date, medium kind, headline) from the same outlet. Not the headline alone: one
    // story from an agency comes out in several outlets, and each one is its own note. Not the link either: social
    // posts share one address (facebook.com/story.php?...) and one press PDF can hold two stories.
    const outletsByStory = new Map<string, unknown[]>();
    const storyKey = (note: NoteDto) =>
      [note.date, this.normalizeMediaKey(foldText(note.media)), foldText(note.title)].join('|');
    const remember = (note: NoteDto) =>
      outletsByStory.set(storyKey(note), [...(outletsByStory.get(storyKey(note)) ?? []), note.mediaName]);
    const exists = (note: NoteDto) =>
      (outletsByStory.get(storyKey(note)) ?? []).some((outlet) => sameOutlet(outlet, note.mediaName));
    stored.forEach(remember);

    const toInsert: Note[] = [];
    let duplicates = 0;
    for (const note of valid) {
      if (exists(note)) {
        duplicates++;
        continue;
      }
      remember(note);
      // Where the row came from is for the error list, not part of the note.
      const fields: Partial<ImportNotesRequestDto['notes'][number]> = { ...note, clientName };
      delete fields.sheet;
      delete fields.row;
      toInsert.push(this.noteRepo.create(fields));
    }
    if (toInsert.length > 0) {
      await this.noteRepo.insertMany(toInsert);
    }
    return { inserted: toInsert.length, duplicates, errors };
  }

  // Devuelve el rango de fechas min/max de la colección note
  async getMinMaxDates(): Promise<{
    minDate: string | null;
    maxDate: string | null;
  }> {
    // Buscar la nota más antigua y la más reciente por campo "date"
    const minNote = await this.noteRepo.findOne({ order: { date: 'ASC' } });
    const maxNote = await this.noteRepo.findOne({ order: { date: 'DESC' } });
    return {
      minDate: minNote?.date ?? null,
      maxDate: maxNote?.date ?? null,
    };
  }

  /**
   * Deletes a note and, first, the file it has in the archive (the PDF of a web note, the clip of a radio or TV
   * note): the worker deletes whichever of the note's links is its own file there and leaves the rest alone. When
   * that file cannot be deleted the note stays, so deleting can be tried again without leaving the file behind.
   */
  async deleteNote(id: string): Promise<void> {
    const note = ObjectId.isValid(id)
      ? await this.noteRepo.findOneBy({ _id: new ObjectId(id) } as never)
      : null;
    if (!note) {
      throw new Error('La nota no existe.');
    }

    const links = [note.attachment, note.link].filter(
      (value): value is string => typeof value === 'string' && /^https?:\/\//i.test(value),
    );
    if (links.length > 0) {
      const result = await this.callWorker<{ deleted: string[]; failed: string[] }>(
        '/notes/archive/delete',
        { links },
      );
      if (result.failed?.length) {
        throw new Error(
          'No se pudo eliminar el archivo de la nota en el servidor de archivo. Intenta de nuevo en unos minutos.',
        );
      }
    }

    await this.noteRepo.delete(id);
  }

  /**
   * The daily news report of a client (Word): its internet notes of the range, grouped by subtopic, with the
   * client's logo and indicators. Throws when there is no note to report (the message is for the user).
   */
  async dailyReport(
    clientName: string,
    from: string,
    to: string,
  ): Promise<{ fileName: string; content: Buffer }> {
    const day = /^\d{4}-\d{2}-\d{2}$/;
    if (!clientName?.trim() || !day.test(from ?? '') || !day.test(to ?? '') || from > to) {
      throw new Error('Elige el cliente y un rango de fechas válido.');
    }

    const notes = await this.noteRepo.find({
      where: { media: 'internet', clientName, date: { $gte: from, $lte: to } },
    } as never);
    if (notes.length === 0) {
      throw new Error(`No hay notas de internet de ${clientName} en esas fechas.`);
    }

    const client = (await this.clientsService.getClients()).find((candidate) => candidate.name === clientName);
    const content = await buildDailyReport({
      client: {
        name: clientName,
        reportLogo: client?.reportLogo || undefined,
        reportIndicators: client?.reportIndicators,
      },
      from,
      to,
      notes,
    });
    return { fileName: dailyReportFileName(clientName, to), content };
  }

  // Trae todas las notas en un rango de fechas (inclusive)
  async listNotesByDateRange(
    startDate: string,
    endDate: string,
  ): Promise<NoteDto[]> {
    // Se asume formato YYYY-MM-DD
    const notes = await this.noteRepo.find({
      where: {
        date: {
          $gte: startDate,
          $lte: endDate,
        },
      },
      order: { date: 'ASC' },
    });
    return notes;
  }

  /**
   * Calcula el rango de fechas del período anterior
   */
  private getPreviousPeriodRange(
    startDate: string,
    period: DashboardPeriod,
  ): [string, string] {
    const start = moment(startDate, 'YYYY-MM-DD');

    if (period === 'semana') {
      const prevStart = start.clone().subtract(1, 'week').startOf('isoWeek');
      const prevEnd = prevStart.clone().endOf('isoWeek');
      return [prevStart.format('YYYY-MM-DD'), prevEnd.format('YYYY-MM-DD')];
    }

    if (period === 'mes') {
      const prevStart = start.clone().subtract(1, 'month').startOf('month');
      const prevEnd = prevStart.clone().endOf('month');
      return [prevStart.format('YYYY-MM-DD'), prevEnd.format('YYYY-MM-DD')];
    }

    if (period === 'trimestre') {
      const quarterStartMonth = Math.floor(start.month() / 3) * 3;
      const quarterStart = start
        .clone()
        .month(quarterStartMonth)
        .startOf('month');
      const prevEnd = quarterStart.clone().subtract(1, 'day');
      const prevQuarterStartMonth = Math.floor(prevEnd.month() / 3) * 3;
      const prevStart = prevEnd
        .clone()
        .month(prevQuarterStartMonth)
        .startOf('month');
      return [prevStart.format('YYYY-MM-DD'), prevEnd.format('YYYY-MM-DD')];
    }

    // anual
    const prevStart = start.clone().subtract(1, 'year').startOf('year');
    const daysDiff = start.diff(start.clone().startOf('year'), 'days');
    const prevEnd = prevStart.clone().add(daysDiff, 'days');
    return [prevStart.format('YYYY-MM-DD'), prevEnd.format('YYYY-MM-DD')];
  }

  /**
   * Construye recursivamente los últimos N períodos (incluyendo el actual).
   * El resultado queda ordenado: [actual, anterior1, anterior2, ...]
   */
  private getLastNPeriodRanges(
    currentStartDate: string,
    currentEndDate: string,
    period: DashboardPeriod,
    totalPeriods: number,
    acc: Array<{ startDate: string; endDate: string }> = [],
  ): Array<{ startDate: string; endDate: string }> {
    if (totalPeriods <= 0) {
      return acc;
    }

    const nextAcc = [
      ...acc,
      { startDate: currentStartDate, endDate: currentEndDate },
    ];

    if (totalPeriods === 1) {
      return nextAcc;
    }

    const [prevStartDate, prevEndDate] = this.getPreviousPeriodRange(
      currentStartDate,
      period,
    );

    return this.getLastNPeriodRanges(
      prevStartDate,
      prevEndDate,
      period,
      totalPeriods - 1,
      nextAcc,
    );
  }

  /**
   * Calcula el porcentaje de publicaciones directas para comparación
   */
  private calculateDirectPercentage(
    directNotes: number,
    totalNotes: number,
  ): number {
    if (totalNotes === 0) return 0;
    return (directNotes / totalNotes) * 100;
  }

  private getComparisonDirectPercentage(
    currentNotes: NoteDto[],
    previousNotes: NoteDto[],
  ): number {
    const prevTotalNotes = previousNotes.length;
    const prevDirectNotes = previousNotes.filter(
      (n) => n.origin === NoteOrigin.DIRECTA,
    ).length;

    // --- Calcular porcentajes de publicaciones directas ---
    const currentDirectPercentage = this.calculateDirectPercentage(
      currentNotes.filter((n) => n.origin === NoteOrigin.DIRECTA).length,
      currentNotes.length,
    );
    const prevDirectPercentage = this.calculateDirectPercentage(
      prevDirectNotes,
      prevTotalNotes,
    );
    return currentDirectPercentage - prevDirectPercentage;
  }

  private getTableDataBySubtopic(
    notes: NoteDto[],
  ): DashboardDataDto['behavior']['tableData'] {
    // --- tableData: agrupado por subtopic ---
    const tableDataMap = new Map<
      string,
      {
        topic: string;
        subtopic: string;
        origin: string;
        audience: number;
        totalNotes: number;
        [NoteSentiment.POSITIVO]: number;
        [NoteSentiment.NEGATIVO]: number;
        [NoteSentiment.NEUTRO]: number;
      }
    >();

    for (const n of notes) {
      const subtopic = (n.subtopic || 'Sin subtopic').trim();
      const key = subtopic.toLowerCase();

      if (!tableDataMap.has(key)) {
        tableDataMap.set(key, {
          topic: n.topic || 'Sin topic',
          subtopic,
          origin: n.origin || 'Sin origen',
          audience: 0,
          totalNotes: 0,
          [NoteSentiment.NEGATIVO]: 0,
          [NoteSentiment.NEUTRO]: 0,
          [NoteSentiment.POSITIVO]: 0,
        });
      }

      const row = tableDataMap.get(key);
      if (!row) {
        continue;
      }
      row.audience += Number(n.audience ?? 0);

      if (n.sentiment === NoteSentiment.POSITIVO) {
        row[NoteSentiment.POSITIVO] += 1;
      } else if (n.sentiment === NoteSentiment.NEGATIVO) {
        row[NoteSentiment.NEGATIVO] += 1;
      } else {
        row[NoteSentiment.NEUTRO] += 1;
      }
      row.totalNotes += 1;
    }

    const tableData = Array.from(tableDataMap.values())
      .map((row) => ({
        ...row,
        [NoteSentiment.NEGATIVO]: String(row[NoteSentiment.NEGATIVO]),
        [NoteSentiment.NEUTRO]: String(row[NoteSentiment.NEUTRO]),
        [NoteSentiment.POSITIVO]: String(row[NoteSentiment.POSITIVO]),
        totalNotes: row.totalNotes,
      }))
      .sort((a, b) => b.totalNotes - a.totalNotes);

    return tableData;
  }

  private getTotalRow(
    tableData: DashboardDataDto['behavior']['tableData'],
  ): DashboardDataDto['sentiment']['tableByTopic'][0] {
    return tableData.reduce(
      (acc, row) => {
        acc.audience += row.audience;
        acc.totalNotes += row.totalNotes;
        acc[NoteSentiment.POSITIVO] = String(
          Number(acc[NoteSentiment.POSITIVO]) +
            Number(row[NoteSentiment.POSITIVO]),
        );
        acc[NoteSentiment.NEGATIVO] = String(
          Number(acc[NoteSentiment.NEGATIVO]) +
            Number(row[NoteSentiment.NEGATIVO]),
        );
        acc[NoteSentiment.NEUTRO] = String(
          Number(acc[NoteSentiment.NEUTRO]) + Number(row[NoteSentiment.NEUTRO]),
        );
        return acc;
      },
      {
        topic: 'Total',
        subtopic: '',
        audience: 0,
        totalNotes: 0,
        [NoteSentiment.POSITIVO]: '0',
        [NoteSentiment.NEGATIVO]: '0',
        [NoteSentiment.NEUTRO]: '0',
      },
    );
  }
  private getTableDataBtyTopic(
    tableData: DashboardDataDto['behavior']['tableData'],
  ): DashboardDataDto['sentiment']['tableByTopic'] {
    let tableByTopic = Object.values(
      tableData.reduce(
        (acc, row) => {
          const key = row.topic.toLowerCase();
          if (key in acc) {
            acc[key].audience += row.audience;
            acc[key].totalNotes += row.totalNotes;
            acc[key][NoteSentiment.POSITIVO] = String(
              Number(acc[key][NoteSentiment.POSITIVO]) +
                Number(row[NoteSentiment.POSITIVO]),
            );
            acc[key][NoteSentiment.NEGATIVO] = String(
              Number(acc[key][NoteSentiment.NEGATIVO]) +
                Number(row[NoteSentiment.NEGATIVO]),
            );
            acc[key][NoteSentiment.NEUTRO] = String(
              Number(acc[key][NoteSentiment.NEUTRO]) +
                Number(row[NoteSentiment.NEUTRO]),
            );
          } else {
            acc[key] = { ...row, subtopic: '' };
          }
          return acc;
        },
        {} as Record<string, (typeof tableData)[0] & { subtopic: string }>,
      ),
    );

    const tableByTopicTotal = this.getTotalRow(tableByTopic);

    tableByTopic = tableByTopic.sort((a, b) => a.audience - b.audience);

    tableByTopic.push(tableByTopicTotal);
    return tableByTopic;
  }

  private normalizeText(value?: string): string {
    return (value ?? '')
      .normalize('NFD')
      .replaceAll(/[\u0300-\u036f]/g, '')
      .trim()
      .toLowerCase();
  }

  private isPresidentNote(note: Pick<NoteDto, 'topic'>): boolean {
    return this.normalizeText(note.topic).includes('presidente');
  }

  private getMostRepeatedTitleBySentiment(
    notes: NoteDto[],
    sentiment: NoteSentiment,
  ): DashboardDataDto['president']['topImpact'] {
    const titlesMap = new Map<
      string,
      {
        title: string;
        mediaNames: Set<string>;
        repeatCount: number;
        audience: number;
      }
    >();

    for (const note of notes) {
      if (note.sentiment !== sentiment) {
        continue;
      }

      const title = (note.title ?? '').trim();
      if (!title) {
        continue;
      }

      const key = this.normalizeText(title);

      if (!titlesMap.has(key)) {
        titlesMap.set(key, {
          title,
          mediaNames: new Set<string>(),
          repeatCount: 0,
          audience: 0,
        });
      }

      const current = titlesMap.get(key);
      if (!current) {
        continue;
      }
      current.repeatCount += 1;
      current.audience += Number(note.audience ?? 0);

      const mediaName = (note.mediaName ?? '').trim();
      if (mediaName) {
        current.mediaNames.add(mediaName);
      }
    }

    const topTitle = Array.from(titlesMap.values()).sort((a, b) => {
      if (b.repeatCount !== a.repeatCount) {
        return b.repeatCount - a.repeatCount;
      }

      if (b.audience !== a.audience) {
        return b.audience - a.audience;
      }

      return a.title.localeCompare(b.title, 'es');
    })[0];

    if (!topTitle) {
      return null;
    }

    return {
      title: topTitle.title,
      mediaNames: Array.from(topTitle.mediaNames).sort((a, b) =>
        a.localeCompare(b, 'es'),
      ),
      repeatCount: topTitle.repeatCount,
      sentiment,
    };
  }

  /**
   * Obtiene el top 20 de medios que más publicaron, con conteo por sentimiento.
   */
  private getTableByMediaName(notes: NoteDto[]): TableByMediaNameItem[] {
    const map = new Map<
      string,
      {
        mediaName: string;
        [NoteSentiment.NEGATIVO]: number;
        [NoteSentiment.NEUTRO]: number;
        [NoteSentiment.POSITIVO]: number;
        totalNotes: number;
        audience: number;
        isNational: boolean;
      }
    >();

    for (const n of notes) {
      const key = (n.mediaName || 'Sin medio').trim().toLowerCase();
      if (!map.has(key)) {
        map.set(key, {
          mediaName: n.mediaName || 'Sin medio',
          [NoteSentiment.NEGATIVO]: 0,
          [NoteSentiment.NEUTRO]: 0,
          [NoteSentiment.POSITIVO]: 0,
          totalNotes: 0,
          audience: 0,
          isNational: (n.zone ?? '').trim().toLowerCase() === 'nacional',
        });
      }
      const row = map.get(key);
      if (!row) {
        continue;
      }
      if (n.sentiment === NoteSentiment.POSITIVO) {
        row[NoteSentiment.POSITIVO] += 1;
      } else if (n.sentiment === NoteSentiment.NEGATIVO) {
        row[NoteSentiment.NEGATIVO] += 1;
      } else {
        row[NoteSentiment.NEUTRO] += 1;
      }
      row.totalNotes += 1;
      row.audience += Number(n.audience ?? 0);
    }

    return Array.from(map.values()).sort((a, b) => b.totalNotes - a.totalNotes);
  }

  /**
   * Agrupa notas por tipo de medio (media), luego por mediaName con
   * conteos de sentimiento y suma de audiencia total para ese tipo de medio.
   */
  private getTableByMedia(notes: NoteDto[]): MediaGroupItem[] {
    const mediaMap = new Map<
      string,
      { items: Map<string, MediaNameSentimentItem>; totalAudience: number }
    >();

    for (const n of notes) {
      const mediaKey = (n.media || 'Sin medio').trim();
      const mediaNameKey = (n.mediaName || 'Sin medio').trim().toLowerCase();

      if (!mediaMap.has(mediaKey)) {
        mediaMap.set(mediaKey, { items: new Map(), totalAudience: 0 });
      }
      const group = mediaMap.get(mediaKey);
      if (!group) {
        continue;
      }
      group.totalAudience += Number(n.audience ?? 0);

      if (!group.items.has(mediaNameKey)) {
        group.items.set(mediaNameKey, {
          mediaName: n.mediaName || 'Sin medio',
          [NoteSentiment.NEGATIVO]: 0,
          [NoteSentiment.NEUTRO]: 0,
          [NoteSentiment.POSITIVO]: 0,
          totalNotes: 0,
        });
      }
      const row = group.items.get(mediaNameKey);
      if (!row) {
        continue;
      }
      if (n.sentiment === NoteSentiment.POSITIVO) {
        row[NoteSentiment.POSITIVO] += 1;
      } else if (n.sentiment === NoteSentiment.NEGATIVO) {
        row[NoteSentiment.NEGATIVO] += 1;
      } else {
        row[NoteSentiment.NEUTRO] += 1;
      }
      row.totalNotes += 1;
    }

    return Array.from(mediaMap.entries())
      .map(([media, group]) => ({
        media,
        items: Array.from(group.items.values()).sort(
          (a, b) => b.totalNotes - a.totalNotes,
        ),
        totalAudience: group.totalAudience,
      }))
      .sort((a, b) => b.totalAudience - a.totalAudience);
  }

  private getSectionByZone(
    notes: NoteDto[],
  ): DashboardDataDto['sectionByZone'] {
    const zoneMap = new Map<
      string,
      {
        zone: string;
        audience: number;
        totalNotes: number;
        [NoteSentiment.NEGATIVO]: number;
        [NoteSentiment.NEUTRO]: number;
        [NoteSentiment.POSITIVO]: number;
      }
    >();

    for (const note of notes) {
      const zone = (note.zone ?? '').trim();
      const normalizedZone = this.normalizeText(zone);

      if (!normalizedZone || normalizedZone === 'nacional') {
        continue;
      }

      if (!zoneMap.has(normalizedZone)) {
        zoneMap.set(normalizedZone, {
          zone,
          audience: 0,
          totalNotes: 0,
          [NoteSentiment.NEGATIVO]: 0,
          [NoteSentiment.NEUTRO]: 0,
          [NoteSentiment.POSITIVO]: 0,
        });
      }

      const row = zoneMap.get(normalizedZone);
      if (!row) {
        continue;
      }

      row.audience += Number(note.audience ?? 0);
      row.totalNotes += 1;

      if (note.sentiment === NoteSentiment.POSITIVO) {
        row[NoteSentiment.POSITIVO] += 1;
      } else if (note.sentiment === NoteSentiment.NEGATIVO) {
        row[NoteSentiment.NEGATIVO] += 1;
      } else {
        row[NoteSentiment.NEUTRO] += 1;
      }
    }

    return {
      tableByZone: Array.from(zoneMap.values()).sort(
        (a, b) => b.totalNotes - a.totalNotes,
      ),
    };
  }

  /**
   * Calcula los datos agrupados del dashboard por sección.
   * Evita enviar toda la lista de notas al frontend.
   */
  async getDashboardData(
    startDate: string,
    endDate: string,
    period: DashboardPeriod = 'semana',
  ): Promise<DashboardDataDto> {
    const notes = await this.listNotesByDateRange(startDate, endDate);
    const directNotes = notes.filter((n) => n.origin === NoteOrigin.DIRECTA);
    const regionalDirectNotes = directNotes.filter((note) => {
      const normalizedZone = this.normalizeText(note.zone);
      return Boolean(normalizedZone) && normalizedZone !== 'nacional';
    });
    const presidentNotes = notes.filter((note) => this.isPresidentNote(note));

    // --- Obtener datos de los últimos 4 períodos (incluye actual) ---
    const last4PeriodRanges = this.getLastNPeriodRanges(
      startDate,
      endDate,
      period,
      4,
    );

    const notesByPeriod = await Promise.all(
      last4PeriodRanges.map((range) =>
        this.listNotesByDateRange(range.startDate, range.endDate),
      ),
    );
    const prevNotes = notesByPeriod[1] ?? [];

    // --- Sección 1: Comportamiento (sentimiento) ---
    const sentimentCounts: Record<string, number> = {};
    for (const n of directNotes) {
      const key = n.sentiment || 'Sin sentimiento';
      sentimentCounts[key] = (sentimentCounts[key] || 0) + 1;
    }
    const sentimentData = Object.entries(sentimentCounts).map(
      ([type, value]) => ({ type, value }),
    );
    // --- Sección 2: Sentimiento ---
    const tableDataSubtopic = this.getTableDataBySubtopic(notes);
    const presidentTableData = tableDataSubtopic.filter((row) =>
      row.topic.toLowerCase().includes('presidente'),
    );
    const directTableDataSubtopic = this.getTableDataBySubtopic(directNotes);
    // --- tableByTopic: agrupado por topic a partir de tableData ---
    const tableByTopic = this.getTableDataBtyTopic([
      ...directTableDataSubtopic,
      ...presidentTableData,
    ]);
    // --- Sección 2: Performance ---

    const tablesByPeriod = last4PeriodRanges.map((range, index) => {
      const periodNotes =
        notesByPeriod[index].filter((n) => n.origin === NoteOrigin.DIRECTA) ??
        [];
      return {
        startDate: range.startDate,
        endDate: range.endDate,
        tableData: this.getTableDataBySubtopic(periodNotes).slice(0, 5),
      };
    });

    const resultsByPeriod = last4PeriodRanges.map((range, index) => {
      const periodNotes =
        notesByPeriod[index].filter((n) => n.origin === NoteOrigin.DIRECTA) ??
        [];
      return {
        startDate: range.startDate,
        endDate: range.endDate,
        tableData: [this.getTotalRow(this.getTableDataBySubtopic(periodNotes))],
      };
    });
    return {
      period,
      behavior: {
        totalNotes: notes.length,
        directNotes: notes.filter((n) => n.origin === NoteOrigin.DIRECTA)
          .length,
        indirectNotes: notes.filter((n) => n.origin === NoteOrigin.INDIRECTA)
          .length,
        tableData: directTableDataSubtopic,
        sentimentData,
        comparisonDirectPercentage: this.getComparisonDirectPercentage(
          notes,
          prevNotes,
        ),
      },
      sentiment: {
        subTopicTop5: directTableDataSubtopic.slice(0, 5),
        tableByTopic,
      },
      performance: {
        resultsByPeriod,
        tablesByPeriod,
      },
      sectionByZone: this.getSectionByZone(regionalDirectNotes),
      tableByMediaName: this.getTableByMediaName(directNotes),
      tableByMedia: this.getTableByMedia(directNotes),
      president: {
        tableByMediaName: this.getTableByMediaName(presidentNotes),
        totalNotes: presidentNotes.length,
        totalAudience: presidentNotes.reduce(
          (acc, note) => acc + Number(note.audience ?? 0),
          0,
        ),
        positiveNotes: presidentNotes.filter(
          (note) => note.sentiment === NoteSentiment.POSITIVO,
        ).length,
        neutralNotes: presidentNotes.filter(
          (note) => note.sentiment === NoteSentiment.NEUTRO,
        ).length,
        negativeNotes: presidentNotes.filter(
          (note) => note.sentiment === NoteSentiment.NEGATIVO,
        ).length,
        topImpact: this.getMostRepeatedTitleBySentiment(
          presidentNotes,
          NoteSentiment.NEGATIVO,
        ),
      },
    };
  }
}
