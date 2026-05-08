import { Module } from '@nestjs/common';
import { ClientsController } from './clients.controller';
import { ClientsService } from './clients.service';
import { FirebaseAdminModule } from '../firebase/firebase-admin.module';

@Module({
  imports: [FirebaseAdminModule],
  controllers: [ClientsController],
  providers: [ClientsService],
})
export class ClientsModule {}
