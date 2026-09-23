import { Module } from '@nestjs/common';
import { StatusService } from './status.service';
import { StatusController } from './status.controller';
import { StatusGateway } from './status.gateway';

@Module({
  providers: [StatusService, StatusGateway],
  controllers: [StatusController],
})
export class StatusModule {}
