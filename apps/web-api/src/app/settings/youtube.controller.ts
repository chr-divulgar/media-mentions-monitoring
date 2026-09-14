import {
  Controller,
  Get,
  Post,
  Body,
  HttpException,
  HttpStatus,
  Logger,
} from '@nestjs/common';
import { YouTubeService } from './youtube.service';
import {
  YouTubeStatusDto,
  SaveCookiesDto,
  SaveCookiesResponseDto,
  AuthInstructionsDto,
  ExtractCookiesDto,
  ExtractCookiesResponseDto,
  StartYouTubeLoginSessionResponseDto,
} from '@repo/shared';

@Controller('settings')
export class YouTubeController {
  private readonly logger = new Logger(YouTubeController.name);

  constructor(private readonly youtubeService: YouTubeService) {}

  /**
   * Get YouTube health status and cookies validation.
   */
  @Get('youtube/status')
  async getYouTubeStatus(): Promise<YouTubeStatusDto> {
    try {
      const status = await this.youtubeService.getYouTubeStatus();
      this.logger.log(
        `YouTube status retrieved: ${status.status}, Excluded sources: ${status.excludedYouTubeSources.length}`,
      );
      return status;
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`Error checking YouTube status: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Error checking YouTube status: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  /**
   * Refresh YouTube cookies from uploaded content.
   */
  @Post('youtube/save-cookies')
  async saveYouTubeCookies(
    @Body() request: SaveCookiesDto,
  ): Promise<SaveCookiesResponseDto> {
    if (!request.cookiesContent || request.cookiesContent.trim().length === 0) {
      throw new HttpException(
        {
          status: HttpStatus.BAD_REQUEST,
          error: 'Cookies content is required',
        },
        HttpStatus.BAD_REQUEST,
      );
    }

    try {
      const response = await this.youtubeService.saveCookies(
        request.cookiesContent,
      );
      this.logger.log('YouTube cookies saved successfully');
      return response;
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`Error saving cookies: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.BAD_REQUEST,
          error: `Error saving cookies: ${errorMsg}`,
        },
        HttpStatus.BAD_REQUEST,
      );
    }
  }

  /**
   * Get authentication instructions and URLs.
   */
  @Get('youtube/auth-instructions')
  getAuthInstructions(): AuthInstructionsDto {
    return {
      youtubeUrl: 'https://www.youtube.com/',
      extensionUrl: 'https://www.youtube.com/',
      steps: [
        { step: 1, action: 'Click Start Login Session from this page' },
        { step: 2, action: 'A browser window opens (may use a temporary profile if your Edge is running)' },
        { step: 3, action: 'Sign in to Google/YouTube if needed' },
        { step: 4, action: 'Return here and click Obtain Cookies' },
      ],
      alternativeExtensions: [],
      format: 'Netscape HTTP Cookie File (text format)',
    };
  }

  /**
   * Start a manual login session in a controlled browser window.
   */
  @Post('youtube/login-session/start')
  async startManualLoginSession(): Promise<StartYouTubeLoginSessionResponseDto> {
    try {
      return await this.youtubeService.startManualLoginSession();
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`[YouTubeController] Failed to start login session: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Failed to start login session: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  /**
   * Extract cookies from a previously started session.
   */
  @Post('youtube/login-session/obtain-cookies')
  async extractCookiesAutomatically(
    @Body() dto?: ExtractCookiesDto,
  ): Promise<ExtractCookiesResponseDto> {
    try {
      const result = await this.youtubeService.extractCookiesFromSession(dto?.sessionId);

      if (!result.success) {
        throw new HttpException(
          {
            status: HttpStatus.UNPROCESSABLE_ENTITY,
            error: result.message,
          },
          HttpStatus.UNPROCESSABLE_ENTITY,
        );
      }

      this.logger.log(`[YouTubeController] Cookies obtained: ${result.extractedCookieCount}`);

      return result;
    } catch (error) {
      if (error instanceof HttpException) throw error;

      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`[YouTubeController] Auto-extraction failed: ${errorMsg}`);

      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Failed to extract cookies: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  /**
   * Cancel manual login session.
   */
  @Post('youtube/login-session/cancel')
  async cancelManualLoginSession(@Body() dto?: ExtractCookiesDto): Promise<{ success: boolean }> {
    await this.youtubeService.cancelManualLoginSession(dto?.sessionId);
    return { success: true };
  }

  /**
   * Send extracted cookies to the worker via HTTP.
   */
  @Post('youtube/sync-to-worker')
  async sendCookiesToWorker(): Promise<{ success: boolean; message: string }> {
    try {
      const result = await this.youtubeService.sendCookiesToWorker();
      this.logger.log(`[YouTubeController] Cookies synced to worker: ${result.success}`);
      return result;
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`[YouTubeController] Failed to sync cookies to worker: ${errorMsg}`);
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `Failed to sync cookies to worker: ${errorMsg}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
