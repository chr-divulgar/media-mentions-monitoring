import { Injectable } from '@nestjs/common';
import { AudioFile, CreateFileDto, getDateFromFile } from '@repo/shared';
import * as ffmpeg from 'fluent-ffmpeg';
import * as fs from 'fs';
import * as path from 'path';
import { Readable } from 'stream';
import { pipeline } from 'stream/promises';
import {
  audioOffsetInClip,
  isSafeClipName,
  segmentQuery,
  segmentStartSeconds,
} from './video.helpers';

/**
 * The video side of "edit alert / create note" for TV alerts: the worker cuts a window out of the
 * channel's recorded MP4 (local, or fetched from the archive server for earlier days — its job, not
 * this service's) and this service stores it next to the audio clips so the editor can play it, and
 * extracts the audio the transcription still needs from the final clip.
 */
@Injectable()
export class VideoService {
  // Long enough to cover the worker downloading an hour of video from the archive server first.
  private static readonly WORKER_TIMEOUT_MS = 5 * 60 * 1000;

  async createVideoFile(createFileDto: CreateFileDto): Promise<AudioFile> {
    const { alert, duration } = createFileDto;
    if (!alert) throw new Error('Alert not found');
    if (alert.source !== 'worker') {
      throw new Error('El video de TV solo existe para alertas del worker.');
    }

    const output = createFileDto.output ?? '';
    if (!isSafeClipName(output)) throw new Error('Nombre de archivo no válido.');

    const outputPath = path.resolve(`./audioFiles/${output}.mp4`);
    const filePath = alert.filePath ?? '';

    if (output.includes('segment')) {
      return this.createSegment(
        this.sourceIdOf(filePath),
        getDateFromFile(filePath),
        new Date(alert.endTime ?? ''),
        duration ?? 0,
        outputPath,
      );
    }
    if (output.includes('fragment')) {
      return this.createFragment(
        this.sourceIdOf(filePath),
        getDateFromFile(filePath),
        createFileDto.startSecond ?? 0,
        duration ?? 0,
        outputPath,
      );
    }

    throw new Error('No valid type to get Video');
  }

  // The wide window the editor works in, centred on the end of the alert. Returns where the clip
  // really starts (a keyframe, a little earlier than asked) relative to the alert's hour file, so
  // the editor's "file time + startSeconds + position in the player" maths stays exact.
  private async createSegment(
    sourceId: string,
    fileTime: Date,
    alertEnd: Date,
    windowSeconds: number,
    outputPath: string,
  ): Promise<AudioFile> {
    const endSeconds = (alertEnd.getTime() - fileTime.getTime()) / 1000;
    const windowStart = segmentStartSeconds(endSeconds, windowSeconds);

    const clip = await this.fetchClip(
      sourceId,
      new Date(fileTime.getTime() + windowStart * 1000),
      new Date(fileTime.getTime() + (windowStart + windowSeconds) * 1000),
      outputPath,
      false,
    );

    return {
      startSeconds: (clip.startUtc.getTime() - fileTime.getTime()) / 1000,
      duration: clip.durationSeconds,
    };
  }

  // The window the user picked: the MP4 clip, plus the mp3 and wav the transcription and summary
  // flow expects, taken from exactly the selected window (not the keyframe lead-in).
  private async createFragment(
    sourceId: string,
    fileTime: Date,
    startSecond: number,
    durationSeconds: number,
    outputPath: string,
  ): Promise<AudioFile> {
    const selectedStart = new Date(fileTime.getTime() + startSecond * 1000);
    // exact: the note's clip starts and ends on the instants the user chose, not on the keyframe before the start.
    const clip = await this.fetchClip(
      sourceId,
      selectedStart,
      new Date(selectedStart.getTime() + durationSeconds * 1000),
      outputPath,
      true,
    );

    const offset = audioOffsetInClip(clip.startUtc, selectedStart);
    await Promise.all([
      this.extractAudio(outputPath, outputPath.replace(/\.mp4$/, '.mp3'), offset, durationSeconds, 'mp3'),
      this.extractAudio(outputPath, outputPath.replace(/\.mp4$/, '.wav'), offset, durationSeconds, 'wav'),
    ]);

    return { startSeconds: startSecond, duration: clip.durationSeconds };
  }

  // Streams the worker's clip to disk: tens of MB, never held in memory.
  private async fetchClip(
    sourceId: string,
    startUtc: Date,
    endUtc: Date,
    outputPath: string,
    exact: boolean,
  ): Promise<{ startUtc: Date; durationSeconds: number }> {
    const workerUrl =
      process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
    const query = segmentQuery(sourceId, startUtc, endUtc, exact);

    const response = await fetch(`${workerUrl}/video/segment?${query}`, {
      signal: AbortSignal.timeout(VideoService.WORKER_TIMEOUT_MS),
    });

    if (!response.ok || !response.body) {
      const body = await response.text().catch(() => '');
      let message = `El servicio de video respondió ${response.status}`;
      try {
        const parsed = JSON.parse(body) as { message?: string };
        if (parsed.message) message = parsed.message;
      } catch {
        // Not JSON — keep the generic message above.
      }
      throw new Error(message);
    }

    await fs.promises.mkdir(path.dirname(outputPath), { recursive: true });
    await pipeline(
      Readable.fromWeb(response.body as never),
      fs.createWriteStream(outputPath),
    );

    return {
      startUtc: new Date(
        response.headers.get('x-clip-start-utc') ?? startUtc.toISOString(),
      ),
      durationSeconds: Number(
        response.headers.get('x-clip-duration-seconds') ?? 0,
      ),
    };
  }

  // Audio only, same settings the audio flow uses for its fragment (mono 16 kHz).
  private extractAudio(
    inputPath: string,
    outputPath: string,
    offsetSeconds: number,
    durationSeconds: number,
    format: 'mp3' | 'wav',
  ): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const command = ffmpeg(inputPath)
        .setStartTime(offsetSeconds)
        .setDuration(durationSeconds)
        .noVideo()
        .audioFrequency(16000)
        .audioChannels(1)
        .toFormat(format)
        .output(outputPath);

      if (format === 'mp3') command.audioBitrate(32).audioCodec('libmp3lame');

      command.on('end', () => resolve()).on('error', reject).run();
    });
  }

  // ".../rcn_tv_2026-10-05_21-00-00.opus" -> "rcn_tv" (everything before the last two "_" parts).
  private sourceIdOf(filePath: string): string {
    const base = path.basename(filePath, path.extname(filePath));
    return base.split('_').slice(0, -2).join('_');
  }

  async getClipByName(
    filename: string,
  ): Promise<{ path: string; size: number } | null> {
    if (!isSafeClipName(filename)) return null;
    const filePath = path.resolve(`./audioFiles/${filename}.mp4`);
    try {
      const { size } = await fs.promises.stat(filePath);
      return { path: filePath, size };
    } catch {
      return null;
    }
  }
}
