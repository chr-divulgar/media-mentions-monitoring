import {
  Controller,
  Post,
  HttpStatus,
  Body,
  HttpException,
  Get,
  Param,
} from '@nestjs/common';
import { SettingsService } from './settings.service';
import { PlatformDto } from '@repo/shared';
import { Platform } from '../entities';

@Controller('settings')
export class SettingsController {
  constructor(private readonly settingsService: SettingsService) {}

  // ─── Platforms ────────────────────────────────────────────────────────────

  @Get('get-platforms/:media')
  async getPlatforms(@Param('media') media: string): Promise<Platform[]> {
    try {
      return await this.settingsService.getPlatforms(media);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: error instanceof Error ? error.message : String(error),
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('create-platform')
  async createPlatform(@Body() dto: PlatformDto): Promise<Platform> {
    try {
      return await this.settingsService.createPlatform(dto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: error instanceof Error ? error.message : String(error),
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('update-platform')
  async updatePlatform(@Body() dto: PlatformDto): Promise<Platform> {
    try {
      return await this.settingsService.updatePlatform(dto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: error instanceof Error ? error.message : String(error),
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
