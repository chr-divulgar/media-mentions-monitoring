import { Module } from '@nestjs/common';
import { SettingsService } from './settings.service';
import { SettingsController } from './settings.controller';
import { YouTubeController } from './youtube.controller';
import { YouTubeService } from './youtube.service';

@Module({
  controllers: [SettingsController, YouTubeController],
  providers: [SettingsService, YouTubeService],
})
export class SettingsModule {}
