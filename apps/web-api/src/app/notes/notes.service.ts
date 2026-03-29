import { Injectable } from '@nestjs/common';
import { DataSource, MongoRepository } from 'typeorm';
import { ObjectId } from 'mongodb';
import { NoteDto } from '@repo/shared';
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
}
