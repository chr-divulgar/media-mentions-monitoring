import { Module } from '@nestjs/common';
import { VideoController } from './video.controller';
import { VideoService } from './video.service';

@Module({
  providers: [VideoService],
  controllers: [VideoController],
})
export class VideoModule {}
