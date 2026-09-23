import { Body, Controller, HttpException, HttpStatus, Post } from '@nestjs/common';
import { GetCaptureStatusDto } from '@repo/shared';
import { StatusService } from './status.service';

@Controller('status')
export class StatusController {
  constructor(private readonly statusService: StatusService) {}

  @Post('capture')
  async getCaptureStatus(@Body() dto: GetCaptureStatusDto) {
    try {
      return await this.statusService.getCaptureStatus(dto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getCaptureStatus ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
