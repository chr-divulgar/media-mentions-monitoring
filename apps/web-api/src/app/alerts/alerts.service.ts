// alerta.service.ts
import { Injectable } from '@nestjs/common';
import {
  DataSource,
  FindOneOptions,
  FindOptionsWhere,
  MongoRepository,
} from 'typeorm';
import {
  GetAlertsDto,
  GetSummaryDto,
  GetTranscriptionDto,
  SummaryDto,
  TranscriptionDto,
  ValidDatesDto,
} from '@repo/shared';
import { splitAudio } from './audio-chunker';
import { transcribeWithPython } from './google-speech';
import OpenAI from 'openai';
import { exec } from 'child_process';
import * as path from 'path';
import { ConfigService } from '@nestjs/config';
import { Alert, Note, Transcription, WorkerAlert } from '../entities';
import { InjectDataSource } from '@nestjs/typeorm';
import * as fs from 'fs/promises';

@Injectable()
export class AlertsService {
  alertsRepo: MongoRepository<Alert>;
  workerAlertsRepo: MongoRepository<WorkerAlert>;
  transcriptRepo: MongoRepository<Transcription>;
  noteRepo: MongoRepository<Note>;
  openai: OpenAI;

  constructor(
    @InjectDataSource('monitoring') private readonly dataSource: DataSource,
    configService: ConfigService,
  ) {
    this.alertsRepo = this.dataSource.getMongoRepository(Alert);
    this.workerAlertsRepo = this.dataSource.getMongoRepository(WorkerAlert);
    this.transcriptRepo = this.dataSource.getMongoRepository(Transcription);
    this.noteRepo = this.dataSource.getMongoRepository(Note);
    this.openai = new OpenAI({
      apiKey: configService.get<string>('OPEN_AI_KEY'),
    });
  }

  async getChatResponse(getSummaryDto: GetSummaryDto): Promise<SummaryDto> {
    try {
      const prompt = `Genera un título separado por una línea nueva y un resumen en forma de noticia escrita en tercera persona,
      el resumen máximo alrededor de 120 palabras que sea coherente y bien redactado. 
      Asegúrate de que el resumen sea fiel a los hechos y no atribuya incorrectamente acciones o eventos a personas o entidades mencionadas en el texto.`;

      // return { title: 'Titulo', summary: prompt };

      const response = await this.openai.responses.create({
        model: 'gpt-5-mini',
        instructions: prompt,
        input: getSummaryDto.text,
      });

      const message = response.output_text.replace(/#/g, '').replace(/\*/g, '');
      const title = message.split('\n')[0].trim();
      const summary = message.split('\n').slice(1).join('\n').trim();

      await this.noteRepo.update(
        { id: getSummaryDto.noteId },
        { title, summary },
      );

      return { title, summary };
    } catch (error) {
      throw new Error(`Error fetching response from OpenAI.${error}`);
    }
  }

  async runPythonScript(scriptPath: string, args: string[]): Promise<string> {
    return new Promise((resolve, reject) => {
      const command = `python ${scriptPath} ${args.join(' ')}`;
      exec(command, { encoding: 'utf8' }, (error, stdout, stderr) => {
        if (error) {
          reject(new Error(`Error: ${error.message}`));
          return;
        }
        if (stderr) {
          reject(new Error(`Stderr: ${stderr}`));
          return;
        }
        resolve(stdout);
      });
    });
  }

  async getAlerts(getAlertasDto: GetAlertsDto) {
    if (!getAlertasDto) {
      throw new Error('getAlertasDto is undefined');
    }
    const {
      startDate = '',
      endDate = '',
      clientName = '',
      type = [],
      source,
    } = getAlertasDto;

    if (!startDate || !endDate) {
      throw new Error('startDate and endDate are required');
    }

    // Legacy stores local (Bogotá) wall-clock time mislabeled as UTC, so a plain 'Z' boundary
    // already lines up with the user's intended calendar day. Worker alerts store real UTC, so
    // the same 'Z' boundary would select a UTC day instead of a Bogotá day — e.g. a worker alert
    // at 2026-09-15T02:17Z (21:17 the previous day in Bogotá) would wrongly count as "today" when
    // the user picks 2026-09-15. Anchor the boundary to -05:00 for worker so it selects the same
    // calendar day the UI displays (see getWorkerDate in AlertsTable.tsx).
    const dayBoundarySuffix = source === 'worker' ? '-05:00' : 'Z';

    const findOptions: FindOneOptions<Alert> = {
      where: {
        endTime: {
          $gte: new Date(`${startDate}T00:00:00.000${dayBoundarySuffix}`),
          $lte: new Date(`${endDate}T23:59:59.999${dayBoundarySuffix}`),
        },
        ...(clientName && { clientName }),
        ...(type.length > 0 && { type: { $in: type } }),
      } as unknown as FindOptionsWhere<Alert>,
    };

    // 'source' picks which collection to read (see WorkerAlert/worker-alert.entity.ts); it never
    // changes the shape of the query above, so both tables stay comparable filter-for-filter.
    const repo = source === 'worker' ? this.workerAlertsRepo : this.alertsRepo;
    const results = await repo.find(findOptions as FindOneOptions<WorkerAlert>);
    // Stamp the origin on each record: the audio-cut flow (AudioService) needs it to
    // know whether startTime/endTime are real UTC (worker) or legacy's mislabeled-local-time.
    const resolvedSource = source === 'worker' ? 'worker' : 'legacy';
    return results.map((alert) => ({ ...alert, source: resolvedSource }));
  }

  async getText(
    getTranscriptionDto: GetTranscriptionDto,
  ): Promise<TranscriptionDto> {
    if (!getTranscriptionDto.filename) throw new Error('Filename is required');

    const filePath = path.resolve(
      `./audioFiles/${getTranscriptionDto.filename}`,
    );

    const chunkPaths = await splitAudio(filePath, 60);

    // Procesar todos los chunks en paralelo sin límite de concurrencia
    const texts = await Promise.all(
      chunkPaths.map((chunk) =>
        transcribeWithPython(chunk).catch((e) => {
          console.error('Error transcribing chunk', chunk, e);
          return '--error--' as string;
        }),
      ),
    );
    const fullText = texts.join(' ');

    const chunksDir = path.dirname(chunkPaths[0]);
    try {
      await fs.rm(chunksDir, { recursive: true, force: true });
    } catch (err) {
      console.error('Error deleting chunks directory:', err);
    }

    const alertId = getTranscriptionDto.filename
      .split('_')[1]
      .replace('.wav', '');

    let note = await this.noteRepo.findOne({ where: { alert_id: alertId } });

    if (note) {
      // Si existe, actualiza el texto
      note.text = fullText;
      await this.noteRepo.save(note);
      // Puedes devolver noteId si lo necesitas
    } else {
      // Si no existe, crea una nueva nota
      note = this.noteRepo.create({
        alert_id: alertId,
        text: fullText,
        // agrega otros campos si es necesario
      });
      await this.noteRepo.save(note);
      // Puedes devolver noteId si lo necesitas
    }

    if (!note) throw new Error('Note not found');
    return { noteId: note.id, text: note.text };
  }

  async getValidDates(
    getAlertsDto: Partial<GetAlertsDto>,
  ): Promise<ValidDatesDto> {
    const getAlerts = getAlertsDto || {};
    const { clientName } = getAlerts;
    const query: FindOneOptions<Alert> = {
      where: { ...(clientName && { clientName }) },
    };
    // Get the first document by ascending order of date
    const min = await this.alertsRepo.findOne({
      ...query,
      order: { endTime: 'ASC' },
    });

    // Get the last document by descending order of date
    const max = await this.alertsRepo.findOne({
      ...query,
      order: { endTime: 'DESC' },
    });
    if (min === null || max === null) throw new Error('No alerts found');
    return {
      minDate: this.getDate(min.endTime),
      maxDate: this.getDate(max.endTime),
    };
  }

  getDate(date: Date): string {
    return date.toISOString().split('T')[0];
  }
}
