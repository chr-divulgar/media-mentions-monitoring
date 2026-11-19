import { Module } from '@nestjs/common';
import { AlertsController } from './alerts.controller';
import { AlertsService } from './alerts.service';
import { TypeOrmModule } from '@nestjs/typeorm';
import { Alert, Note, Transcription, WorkerAlert } from '../entities';
import { AiModule } from '../ai/ai.module';

@Module({
  imports: [TypeOrmModule.forFeature([Alert, Transcription, Note, WorkerAlert], 'monitoring'), AiModule],
  controllers: [AlertsController],
  providers: [AlertsService],
})
export class AlertsModule {}
