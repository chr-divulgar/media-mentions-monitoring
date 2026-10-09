import { Global, Module } from '@nestjs/common';
import { ConfigDbService } from './config-db.service';
import { UsersBackupService } from './users-backup.service';

// Global: settings, clients and auth all need the same connection to the config database.
@Global()
@Module({
  providers: [ConfigDbService, UsersBackupService],
  exports: [ConfigDbService, UsersBackupService],
})
export class ConfigDbModule {}
