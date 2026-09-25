import { Controller, Get, HttpException, HttpStatus, Logger } from '@nestjs/common';
import { WhatsAppService } from './whatsapp.service';
import { WhatsAppStatusDto, WhatsAppQrDto } from '@repo/shared';

@Controller('settings')
export class WhatsAppController {
  private readonly logger = new Logger(WhatsAppController.name);

  constructor(private readonly whatsAppService: WhatsAppService) {}

  @Get('whatsapp/status')
  async getWhatsAppStatus(): Promise<WhatsAppStatusDto> {
    try {
      return await this.whatsAppService.getStatus();
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`Error checking WhatsApp status: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Error checking WhatsApp status: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Get('whatsapp/qr')
  async getWhatsAppQr(): Promise<WhatsAppQrDto> {
    try {
      return await this.whatsAppService.getQr();
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`Error fetching WhatsApp QR: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Error fetching WhatsApp QR: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
