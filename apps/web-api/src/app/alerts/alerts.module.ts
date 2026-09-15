import { Module } from '@nestjs/common';
import { AlertsController } from './alerts.controller';
import { AlertsService } from './alerts.service';
import { TypeOrmModule } from '@nestjs/typeorm';
import { ConfigService } from '@nestjs/config';
import { Alert, Note, Transcription, WorkerAlert } from '../entities';

@Module({
  imports: [TypeOrmModule.forFeature([Alert, Transcription, Note, WorkerAlert], 'monitoring')],
  controllers: [AlertsController],
  providers: [AlertsService, ConfigService],
})
export class AlertsModule {}
