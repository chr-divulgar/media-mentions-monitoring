import { Module } from '@nestjs/common';
import { SettingsService } from './settings.service';
import { SettingsController } from './settings.controller';
import { YouTubeController } from './youtube.controller';
import { YouTubeService } from './youtube.service';
import { WhatsAppController } from './whatsapp.controller';
import { WhatsAppService } from './whatsapp.service';

@Module({
  controllers: [SettingsController, YouTubeController, WhatsAppController],
  providers: [SettingsService, YouTubeService, WhatsAppService],
})
export class SettingsModule {}
