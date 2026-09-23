import {
  Controller,
  Post,
  Res,
  HttpStatus,
  Body,
  Get,
  Param,
} from '@nestjs/common';
import { AudioService } from './audio.service';
import { FastifyReply } from 'fastify';
import { CreateFileDto } from '@repo/shared';

@Controller('audio')
export class AudioController {
  constructor(private readonly audioService: AudioService) {}

  @Post('createFile')
  async getAudio(
    @Body() createFileDto: CreateFileDto,
    @Res() res: FastifyReply,
  ): Promise<void> {
    try {
      const { startSeconds, duration } =
        await this.audioService.createAudioFile(createFileDto);
      res.header('Content-Type', 'application/json');
      res.send({ startSeconds, duration });
    } catch (err) {
      res
        .status(HttpStatus.INTERNAL_SERVER_ERROR)
        .send({ error: (err as Error).message, stack: (err as Error).stack });
    }
  }

  @Get('fetchByName/:filename')
  async getAudioByName(
    @Param('filename') filename: string,
    @Res() res: FastifyReply,
  ): Promise<void> {
    let audioFile;
    try {
      audioFile = await this.audioService.getAudioFileByName(filename);
      if (!audioFile) {
        res.status(HttpStatus.NOT_FOUND).send({ error: 'File not found' });
        return;
      }
      res.header('Content-Type', 'audio/mpeg');
      res.header('Content-Length', audioFile.size);
      res.header('Accept-Ranges', 'bytes');
      res.send(audioFile.stream);
    } catch (err) {
      res
        .status(HttpStatus.INTERNAL_SERVER_ERROR)
        .send({ error: (err as Error).message });
    }
  }
}
