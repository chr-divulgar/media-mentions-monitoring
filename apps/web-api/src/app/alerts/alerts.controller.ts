import {
  Controller,
  Post,
  Body,
  HttpException,
  HttpStatus,
  Req,
} from '@nestjs/common';
import { AlertsService } from './alerts.service';
import {
  GetAlertsDto,
  GetSummaryDto,
  GetTranscriptionDto,
  SummaryDto,
} from '@repo/shared';

@Controller('alerts')
export class AlertsController {
  constructor(private readonly alertService: AlertsService) {}

  @Post()
  async getAlerts(@Body() getAlertsDto: GetAlertsDto) {
    try {
      return await this.alertService.getAlerts(getAlertsDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getAlerts ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  // The editor of an alert was opened: the first view is kept for the whole team (the "Vista" status).
  @Post('view')
  async markViewed(
    @Body() body: { alertId?: string },
    @Req() request: { user?: { uid?: string } },
  ) {
    try {
      await this.alertService.markViewed(body?.alertId ?? '', request.user?.uid);
      return { ok: true };
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.BAD_REQUEST,
          error: `There was an error processing the request markViewed ${error}`,
        },
        HttpStatus.BAD_REQUEST,
      );
    }
  }

  @Post('sla')
  async getSlaSummary(@Body() body: { startDate: string; endDate: string }) {
    try {
      return await this.alertService.getSlaSummary(body?.startDate, body?.endDate);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getSlaSummary ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('getText')
  async getText(@Body() getTranscriptionDto: GetTranscriptionDto) {
    try {
      return await this.alertService.getText(getTranscriptionDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getText ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('getSummary')
  async getSummary(@Body() getSummaryDto: GetSummaryDto): Promise<SummaryDto> {
    try {
      return await this.alertService.getChatResponse(getSummaryDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getSummary  ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('dates')
  async getValidDates(@Body() getAlertsDto: Partial<GetAlertsDto>) {
    try {
      return await this.alertService.getValidDates(getAlertsDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getValidDates ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
