import { Injectable } from '@nestjs/common';
import { DataSource, MongoRepository } from 'typeorm';
import { ObjectId } from 'mongodb';
import {
  NoteDto,
  DashboardDataDto,
  DashboardPeriod,
  NoteOrigin,
  NoteSentiment,
  TableByMediaNameItem,
  MediaGroupItem,
  MediaNameSentimentItem,
} from '@repo/shared';
import { Note } from '../entities';
import { InjectDataSource } from '@nestjs/typeorm';
import * as moment from 'moment';
import * as path from 'path';
import * as spawn from 'cross-spawn';

@Injectable()
export class NotesService {
  noteRepo: MongoRepository<Note>;

  constructor(
    @InjectDataSource('monitoring') private readonly dataSource: DataSource,
  ) {
    this.noteRepo = this.dataSource.getMongoRepository(Note);
  }

  async setNote(noteDto: NoteDto): Promise<NoteDto> {
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

    const existingNote = await this.noteRepo.findOneBy({
      _id: new ObjectId(noteDto.id),
    });

    if (!existingNote) {
      throw new Error('Note not found');
    }
    noteDto.message = this.generateMessage(noteDto);
    Object.assign(existingNote, noteDto);
    // Save the updated note
    return await this.noteRepo.save(existingNote);
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

    return `NOTA ${note.index} ${note.program} **${note.title}** ${formatPlainText(note.summary)} ${formatDuration(note.duration)} (${moment(note.startTime).format('h:mm A')})`;
  }

  async sendMessage(noteDto: NoteDto): Promise<boolean> {
    const wasMessageSend = await this.sendWhatsAppMessage(
      noteDto.message,
      '573154421610',
    );

    const audioFilePath = path.resolve(
      `./audioFiles/fragment_${noteDto.alert_id}.mp3`,
    );
    console.log(`Sending audio file: ${audioFilePath}`);

    const wasAudioSend = await this.sendWhatsAppMessage(
      audioFilePath,
      '573154421610',
      true,
    );

    if (!wasMessageSend) {
      throw new Error('Error sending message');
    }
    if (!wasAudioSend) {
      throw new Error('Error sending audio');
    }

    return true;
  }

  async sendWhatsAppMessage(
    content: string,
    phoneNumber: string,
    isAudio: boolean = false,
  ): Promise<boolean> {
    let wasSend = false;
    try {
      const npxPath = 'C:\\Program Files\\nodejs\\npx.cmd'; // Adjust this path as needed

      const args = isAudio
        ? ['mudslide', 'send-file', phoneNumber, content, '--type', 'audio']
        : ['mudslide', 'send', phoneNumber, content];

      const result = spawn.sync(npxPath, args, {
        encoding: 'utf-8',
      });

      if (result.error || result.stderr || result.status !== 0) {
        throw new Error(
          `Error sending WhatsApp ${isAudio ? 'audio' : 'message'}: ${result.stderr}`,
        );
      } else {
        wasSend = true;
      }
    } catch (e) {
      throw new Error(`Exception occurred: ${(e as Error).message}`);
    }
    return wasSend;
  }

  async importNotes(notes: NoteDto[]): Promise<{ inserted: number }> {
    if (!Array.isArray(notes)) {
      throw new Error('Input must be an array of notes');
    }
    // Filtrar notas duplicadas por combinación única y validar campos obligatorios
    const insertedNotes: NoteDto[] = [];
    for (const n of notes) {
      if (!n.date || !n.media || !n.mediaName || !n.title) {
        // Saltar notas que no tengan los campos mínimos requeridos
        continue;
      }
      const exists = await this.noteRepo.findOneBy({
        title: n.title,
        date: n.date,
        media: n.media,
        mediaName: n.mediaName,
      });
      if (!exists) {
        insertedNotes.push(this.noteRepo.create(n));
      }
    }
    if (insertedNotes.length > 0) {
      await this.noteRepo.insertMany(insertedNotes);
    }
    return { inserted: insertedNotes.length };
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

      const row = tableDataMap.get(key)!;
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
          if (!acc[key]) {
            acc[key] = { ...row, subtopic: '' };
          } else {
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
          isNational: (n.zone ?? '').trim().toLowerCase() === 'nacional',
        });
      }
      const row = map.get(key)!;
      if (n.sentiment === NoteSentiment.POSITIVO) {
        row[NoteSentiment.POSITIVO] += 1;
      } else if (n.sentiment === NoteSentiment.NEGATIVO) {
        row[NoteSentiment.NEGATIVO] += 1;
      } else {
        row[NoteSentiment.NEUTRO] += 1;
      }
      row.totalNotes += 1;
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
      const group = mediaMap.get(mediaKey)!;
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
      const row = group.items.get(mediaNameKey)!;
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
      tableByMediaName: this.getTableByMediaName(notes),
      tableByMedia: this.getTableByMedia(notes),
    };
  }
}
