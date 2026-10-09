import { Module } from '@nestjs/common';
import { NotesController } from './notes.controller';
import { NotesService } from './notes.service';
import { TypeOrmModule } from '@nestjs/typeorm';
import { ConfigService } from '@nestjs/config';
import { Note, WorkerAlert } from '../entities';
import { ClientsModule } from '../clients/clients.module';
import { SettingsModule } from '../settings/settings.module';

@Module({
  imports: [
    TypeOrmModule.forFeature([Note, WorkerAlert], 'monitoring'),
    ClientsModule,
    SettingsModule,
  ],
  controllers: [NotesController],
  providers: [NotesService, ConfigService],
})
export class NotesModule {}
