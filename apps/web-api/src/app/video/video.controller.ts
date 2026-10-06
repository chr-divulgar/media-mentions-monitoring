import {
  Body,
  Controller,
  Get,
  Headers,
  HttpStatus,
  Param,
  Post,
  Res,
} from '@nestjs/common';
import { FastifyReply } from 'fastify';
import * as fs from 'fs';
import { CreateFileDto } from '@repo/shared';
import { VideoService } from './video.service';
import { parseByteRange } from './video.helpers';

@Controller('video')
export class VideoController {
  constructor(private readonly videoService: VideoService) {}

  @Post('createFile')
  async createFile(
    @Body() createFileDto: CreateFileDto,
    @Res() res: FastifyReply,
  ): Promise<void> {
    try {
      const { startSeconds, duration } =
        await this.videoService.createVideoFile(createFileDto);
      res.header('Content-Type', 'application/json');
      res.send({ startSeconds, duration });
    } catch (err) {
      const message = (err as Error).message;
      // "No hay video…" / "no llega hasta…" come from the worker: the editor tells that apart from a
      // real failure to offer the audio flow instead.
      const notAvailable = /no hay video|no llega hasta|no se pudo leer el video/i.test(message);
      res
        .status(notAvailable ? HttpStatus.NOT_FOUND : HttpStatus.INTERNAL_SERVER_ERROR)
        .send({ error: message });
    }
  }

  // The player seeks with Range requests, so unlike the audio route this one honours them.
  @Get('fetchByName/:filename')
  async fetchByName(
    @Param('filename') filename: string,
    @Headers('range') range: string | undefined,
    @Res() res: FastifyReply,
  ): Promise<void> {
    try {
      const clip = await this.videoService.getClipByName(filename);
      if (!clip) {
        res.status(HttpStatus.NOT_FOUND).send({ error: 'File not found' });
        return;
      }

      res.header('Content-Type', 'video/mp4');
      res.header('Accept-Ranges', 'bytes');
      res.header('Cache-Control', 'no-cache');

      const byteRange = parseByteRange(range, clip.size);
      if (byteRange === 'invalid') {
        res.header('Content-Range', `bytes */${clip.size}`);
        res.status(HttpStatus.REQUESTED_RANGE_NOT_SATISFIABLE).send();
        return;
      }

      if (byteRange === null) {
        res.header('Content-Length', clip.size);
        res.send(fs.createReadStream(clip.path));
        return;
      }

      res.status(HttpStatus.PARTIAL_CONTENT);
      res.header('Content-Range', `bytes ${byteRange.start}-${byteRange.end}/${clip.size}`);
      res.header('Content-Length', byteRange.end - byteRange.start + 1);
      res.send(fs.createReadStream(clip.path, byteRange));
    } catch (err) {
      res
        .status(HttpStatus.INTERNAL_SERVER_ERROR)
        .send({ error: (err as Error).message });
    }
  }
}
