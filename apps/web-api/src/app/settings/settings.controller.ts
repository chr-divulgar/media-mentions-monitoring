import {
  Controller,
  Post,
  HttpStatus,
  Body,
  HttpException,
  Get,
  Param,
  Delete,
} from '@nestjs/common';
import { SettingsService } from './settings.service';
import { PlatformDto, PlatformResponseDto } from '@repo/shared';

@Controller('settings')
export class SettingsController {
  constructor(private readonly settingsService: SettingsService) {}

  // ─── Platforms ────────────────────────────────────────────────────────────────

  @Get('get-platforms/:media')
  async getPlatforms(
    @Param('media') media: string,
  ): Promise<PlatformResponseDto[]> {
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
  async createPlatform(@Body() dto: PlatformDto): Promise<PlatformResponseDto> {
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
  async updatePlatform(@Body() dto: PlatformDto): Promise<PlatformResponseDto> {
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

  @Delete('delete-platform/:id')
  async deletePlatform(@Param('id') id: string) {
    try {
      return await this.settingsService.deletePlatform(id);
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

  // ─── Users ────────────────────────────────────────────────────────────────

  @Get('users')
  async getUsers() {
    try {
      return await this.settingsService.getUsers();
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

  @Post('users/update')
  async updateUser(
    @Body()
    dto: {
      uid: string;
      displayName?: string;
      role?: string;
      phone?: string;
      password?: string;
      disabled?: boolean;
    },
  ) {
    try {
      return await this.settingsService.updateUser(dto);
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

  @Delete('users/:uid')
  async deleteUser(@Param('uid') uid: string) {
    try {
      return await this.settingsService.deleteUser(uid);
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
