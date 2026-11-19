import { Module } from '@nestjs/common';
import { ConfigService } from '@nestjs/config';
import { buildTextModel } from './build-text-model';
import { TEXT_MODEL } from './text-model';

@Module({
  providers: [
    {
      provide: TEXT_MODEL,
      useFactory: (config: ConfigService) => buildTextModel((name) => config.get<string>(name)),
      inject: [ConfigService],
    },
  ],
  exports: [TEXT_MODEL],
})
export class AiModule {}
