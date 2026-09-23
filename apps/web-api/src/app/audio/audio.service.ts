// audio.service.ts
import { Injectable } from '@nestjs/common';
import {
  AlertDto,
  AudioFile,
  CreateFileDto,
  getDateFromFile,
} from '@repo/shared';
import * as ffmpeg from 'fluent-ffmpeg';
import * as fs from 'fs';
import * as moment from 'moment';
import * as os from 'os';
import * as path from 'path';
import * as crypto from 'crypto';
import stream from 'stream';

@Injectable()
export class AudioService {
  async createAudioFile(createFileDto: CreateFileDto): Promise<AudioFile> {
    const { alert, duration } = createFileDto;
    if (!alert) throw new Error('Alert not found');

    const filePath = path.resolve(alert.filePath);
    const outputPath = path.resolve(`./audioFiles/${createFileDto.output}.mp3`);

    if (createFileDto.output.includes('segment'))
      return await this.processAudioSegment(
        alert,
        duration,
        filePath,
        outputPath,
      );
    if (createFileDto.output.includes('fragment'))
      return await this.processAudioFragment(
        createFileDto,
        filePath,
        outputPath,
      );

    throw new Error('No valid type to get Audio');
  }

  async processAudioSegment(
    alert: AlertDto,
    durationIn: number,
    filePath: string,
    outputPath: string,
  ): Promise<AudioFile> {
    const fileTime = getDateFromFile(filePath);
    // Worker alerts store real UTC timestamps; legacy alerts store local (Bogotá)
    // wall-clock time mislabeled as UTC, so the 'Z' has to be swapped for the real offset.
    const endTime =
      alert.source === 'worker'
        ? new Date(alert.endTime ?? '')
        : new Date(alert.endTime?.replace('Z', '-05:00') ?? '');
    const endSeconds = (endTime.getTime() - fileTime.getTime()) / 1000;

    const startSeconds =
      endSeconds > durationIn / 2 ? endSeconds - durationIn / 2 : 0;

    const cachedDuration = await this.tryReuseCachedFile(outputPath);
    if (cachedDuration !== null) {
      return { startSeconds, duration: cachedDuration };
    } else {
      const duration =
        alert.source === 'worker'
          ? await this.extractForWindowFromWorker(
              this.deriveSourceId(filePath),
              fileTime,
              startSeconds,
              startSeconds + durationIn,
              16,
              8000,
              outputPath,
              'mp3',
            )
          : await this.extractForWindow(
              filePath,
              startSeconds,
              startSeconds + durationIn,
              durationIn,
              16,
              8000,
              outputPath,
              'mp3',
            );

      return { startSeconds, duration };
    }
  }
  async processAudioFragment(
    createFileDto: CreateFileDto,
    filePath: string,
    outputPath: string,
  ): Promise<AudioFile> {
    const startSeconds = createFileDto.startSecond;
    const windowEnd = startSeconds + createFileDto.duration;
    const isWorkerAlert = createFileDto.alert?.source === 'worker';

    const durations = isWorkerAlert
      ? await Promise.all([
          this.extractForWindowFromWorker(
            this.deriveSourceId(filePath),
            getDateFromFile(filePath),
            startSeconds,
            windowEnd,
            32,
            16000,
            outputPath,
            'mp3',
          ),
          this.extractForWindowFromWorker(
            this.deriveSourceId(filePath),
            getDateFromFile(filePath),
            startSeconds,
            windowEnd,
            64000,
            16000,
            outputPath.replace('mp3', 'wav'),
            'wav',
          ),
        ])
      : await Promise.all([
          this.extractForWindow(
            filePath,
            startSeconds,
            windowEnd,
            createFileDto.duration,
            32,
            16000,
            outputPath,
            'mp3',
          ),
          this.extractForWindow(
            filePath,
            startSeconds,
            windowEnd,
            createFileDto.duration,
            64000,
            16000,
            outputPath.replace('mp3', 'wav'),
            'wav',
          ),
        ]);
    const duration = durations[0];

    return { startSeconds: startSeconds, duration };
  }

  // The worker's own /audio/segment endpoint (mirrors the shape of StatusService.getCaptureStatus's
  // worker-HTTP call — same env var, same plain fetch, no dedicated client class). It owns every
  // decision about what is readable: closed hours it reads directly, and the hour it is still
  // recording it serves from a point-in-time copy of that file. There is deliberately no local
  // pre-check here — one used to reject live-hour windows outright, which now just blocks
  // requests the worker can actually fulfil.
  private async extractForWindowFromWorker(
    sourceId: string,
    fileTime: Date,
    windowStart: number,
    windowEnd: number,
    audioBitrate: number,
    audioFrequency: number,
    outputPath: string,
    format: string,
  ): Promise<number> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const startUtc = new Date(fileTime.getTime() + windowStart * 1000).toISOString();
    const endUtc = new Date(fileTime.getTime() + windowEnd * 1000).toISOString();
    const query = new URLSearchParams({
      sourceId,
      startUtc,
      endUtc,
      bitrateKbps: String(audioBitrate),
      frequencyHz: String(audioFrequency),
      format,
    });

    const response = await fetch(`${workerUrl}/audio/segment?${query}`, {
      signal: AbortSignal.timeout(30000),
    });

    if (!response.ok) {
      const body = await response.text().catch(() => '');
      let message = `Worker audio service responded ${response.status}`;
      try {
        const parsed = JSON.parse(body) as { message?: string };
        if (parsed.message) message = parsed.message;
      } catch {
        // Not JSON — fall through to the generic message above.
      }
      throw new Error(message);
    }

    await fs.promises.writeFile(outputPath, Buffer.from(await response.arrayBuffer()));
    return this.getAudioDuration(outputPath);
  }

  // Recovers the sourceId a worker-produced filename encodes, e.g.
  // ".../radio-a_2026-09-22_11-00-00.opus" -> "radio-a" — the same parsing resolveSourceFiles
  // already does internally to find sibling hourly files.
  private deriveSourceId(filePath: string): string {
    const base = path.basename(filePath, path.extname(filePath));
    const parts = base.split('_');
    return parts.length < 3 ? base : parts.slice(0, -2).join('_');
  }

  // Resolves the real source file(s) for [windowStart, windowEnd] (seconds relative to
  // anchorFilePath's own start) and extracts through the single- or multi-file path as needed.
  private async extractForWindow(
    anchorFilePath: string,
    windowStart: number,
    windowEnd: number,
    duration: number,
    audioBitrate: number,
    audioFrequency: number,
    outputPath: string,
    format: string,
  ): Promise<number> {
    const { files, startOffsetSec } = this.resolveSourceFiles(
      anchorFilePath,
      windowStart,
      windowEnd,
    );

    return files.length === 1
      ? this.extractAudio(
          anchorFilePath,
          windowStart,
          duration,
          audioBitrate,
          audioFrequency,
          outputPath,
          format,
        )
      : this.extractAudioConcat(
          files,
          startOffsetSec,
          duration,
          audioBitrate,
          audioFrequency,
          outputPath,
          format,
        );
  }

  // Source recordings rotate hourly (see stage/worker-options.json defaultOpusRotationIntervalHours
  // for the worker; the legacy recorder's rotation interval is unknown/out of this repo, but uses the
  // same filename shape). A requested window can spill into the previous/next hourly file, so walk
  // outward from the anchor filename and only include siblings that actually exist on disk — this
  // degrades to today's single-file behavior whenever no sibling is found (including for any legacy
  // recorder whose files are already longer than the requested window).
  private resolveSourceFiles(
    anchorFilePath: string,
    windowStartSec: number,
    windowEndSec: number,
  ): { files: string[]; startOffsetSec: number } {
    const dir = path.dirname(anchorFilePath);
    const ext = path.extname(anchorFilePath);
    const base = path.basename(anchorFilePath, ext);
    const parts = base.split('_');

    if (parts.length < 3) {
      return { files: [anchorFilePath], startOffsetSec: windowStartSec };
    }

    const prefix = parts.slice(0, -2).join('_');
    const anchorMoment = moment.utc(
      `${parts[parts.length - 2]} ${parts[parts.length - 1]}`,
      'YYYY-MM-DD HH-mm-ss',
    );

    const candidatePath = (deltaHours: number): string => {
      const shifted = anchorMoment.clone().add(deltaHours, 'hours');
      return path.join(
        dir,
        `${prefix}_${shifted.format('YYYY-MM-DD')}_${shifted.format('HH-mm-ss')}${ext}`,
      );
    };

    let hoursBefore = 0;
    while (windowStartSec < -hoursBefore * 3600) {
      if (!fs.existsSync(candidatePath(-(hoursBefore + 1)))) break;
      hoursBefore++;
    }

    let hoursAfter = 0;
    while (windowEndSec > (hoursAfter + 1) * 3600) {
      if (!fs.existsSync(candidatePath(hoursAfter + 1))) break;
      hoursAfter++;
    }

    if (hoursBefore === 0 && hoursAfter === 0) {
      return { files: [anchorFilePath], startOffsetSec: windowStartSec };
    }

    const files: string[] = [];
    for (let h = -hoursBefore; h <= hoursAfter; h++) {
      files.push(h === 0 ? anchorFilePath : candidatePath(h));
    }

    return { files, startOffsetSec: windowStartSec + hoursBefore * 3600 };
  }

  async extractAudio(
    filePath: string,
    startSeconds: number,
    duration: number,
    audioBitrate: number,
    audioFrequency: number,
    outputPath: string,
    format: string,
  ): Promise<number> {
    return this.runExtraction(
      ffmpeg(filePath),
      startSeconds,
      duration,
      audioBitrate,
      audioFrequency,
      outputPath,
      format,
    );
  }

  // Concatenates multiple hourly source files (same sourceId/codec, so no re-encode of the
  // inputs is needed) via ffmpeg's concat demuxer before cutting the requested window.
  private async extractAudioConcat(
    files: string[],
    startSeconds: number,
    duration: number,
    audioBitrate: number,
    audioFrequency: number,
    outputPath: string,
    format: string,
  ): Promise<number> {
    const listPath = path.join(
      os.tmpdir(),
      `concat_${crypto.randomUUID()}.txt`,
    );
    const listContent = files
      .map((f) => `file '${f.replace(/'/g, "'\\''")}'`)
      .join('\n');
    await fs.promises.writeFile(listPath, listContent, 'utf8');

    try {
      const command = ffmpeg()
        .input(listPath)
        .inputOptions(['-f', 'concat', '-safe', '0']);
      return await this.runExtraction(
        command,
        startSeconds,
        duration,
        audioBitrate,
        audioFrequency,
        outputPath,
        format,
      );
    } finally {
      await fs.promises.unlink(listPath).catch(() => undefined);
    }
  }

  private runExtraction(
    command: ffmpeg.FfmpegCommand,
    startSeconds: number,
    duration: number,
    audioBitrate: number,
    audioFrequency: number,
    outputPath: string,
    format: string,
  ): Promise<number> {
    return new Promise<number>((resolve, reject) => {
      command
        .setStartTime(startSeconds)
        .setDuration(duration)
        .audioBitrate(audioBitrate) // Lower bitrate Reduce file size
        .audioFrequency(audioFrequency) // Lower sample rate reduce elapsed time
        .audioChannels(1) // Convert to mono
        .outputOptions('-preset ultrafast')
        .toFormat(format)
        .output(outputPath);

      if (format == 'mp3') {
        command.audioCodec('libmp3lame');
      }

      command
        .on('end', function () {
          // Usar ffprobe para obtener la duración del archivo procesado
          ffmpeg.ffprobe(outputPath, (err, metadata) => {
            if (err) {
              reject(new Error(err));
            } else {
              resolve(metadata.format.duration ?? 0);
            }
          });
        })
        .on('error', function (err) {
          return reject(err);
        })
        .run();
    });
  }

  async getAudioFileByName(
    filename: string,
  ): Promise<{ stream: stream.Readable; size: number } | null> {
    const filePath = path.resolve(`./audioFiles/${filename}.mp3`);
    if (!fs.existsSync(filePath)) return null;
    try {
      const { size } = await fs.promises.stat(filePath);
      return { stream: fs.createReadStream(filePath), size };
    } catch (error) {
      throw new Error(String(error));
    }
  }

  /**
   * Gets the duration of an audio file using ffprobe from fluent-ffmpeg.
   *
   * @param filePath The path to the audio file.
   * @returns A promise that resolves with the duration of the audio file in seconds.
   */
  async getAudioDuration(filePath: string): Promise<number> {
    return new Promise<number>((resolve, reject) => {
      ffmpeg.ffprobe(filePath, (err, metadata) => {
        if (err) reject(new Error(err));
        else resolve(metadata.format.duration ?? 0);
      });
    });
  }

  // A cached file from an earlier run can be corrupt or truncated (a worker fetch interrupted
  // mid-write, or — before this pipeline read only closed hours — a read of the source
  // recording's still-open current hour) and existence alone doesn't catch that; every future
  // request for that alert would keep serving the same bad file forever. Probing it is cheap and
  // is what lets a single bad historical write self-heal instead of poisoning the cache
  // permanently.
  private async tryReuseCachedFile(outputPath: string): Promise<number | null> {
    if (!(await this.checkFileExists(outputPath))) return null;

    try {
      const duration = await this.getAudioDuration(outputPath);
      if (duration > 0) return duration;
    } catch {
      // Falls through to treat it as unusable below.
    }

    await fs.promises.unlink(outputPath).catch(() => undefined);
    return null;
  }

  async checkFileExists(filePath: string): Promise<boolean> {
    try {
      await fs.promises.access(filePath, fs.constants.F_OK);
      return true; // El archivo existe
    } catch {
      return false; // El archivo no existe
    }
  }
}
