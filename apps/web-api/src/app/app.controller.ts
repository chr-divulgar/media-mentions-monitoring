import { Controller, Get } from '@nestjs/common';

import { AppService } from './app.service';
import { Public } from './auth/auth.decorators';

// The root route also serves the SPA entry, which must load before anyone has signed in.
@Public()
@Controller()
export class AppController {
  constructor(private readonly appService: AppService) {}

  @Get()
  getData() {
    return this.appService.getData();
  }
}
