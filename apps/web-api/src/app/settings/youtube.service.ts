import { Injectable, Logger } from '@nestjs/common';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';
import puppeteer from 'puppeteer';
import type { Browser, Page } from 'puppeteer';
import {
  YouTubeStatusDto,
  SaveCookiesResponseDto,
  CookiesValidationDto,
  ExtractCookiesResponseDto,
  StartYouTubeLoginSessionResponseDto,
} from '@repo/shared';

type LoginSession = {
  browser: Browser;
  page: Page;
  createdAt: number;
};

@Injectable()
export class YouTubeService {
  private readonly logger = new Logger(YouTubeService.name);
  private readonly loginSessions = new Map<string, LoginSession>();
  private readonly sessionTtlMs = 15 * 60 * 1000;
  private latestSessionId: string | null = null;
  private readonly browserExecutablePath =
    process.env.YOUTUBE_BROWSER_EXECUTABLE_PATH ||
    path.join(
      process.env['PROGRAMFILES(X86)'] || process.env.PROGRAMFILES || 'C:\\Program Files (x86)',
      'Microsoft',
      'Edge',
      'Application',
      'msedge.exe',
    );
  private readonly browserUserDataDir =
    process.env.YOUTUBE_BROWSER_USER_DATA_DIR ||
    path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'Edge', 'User Data');

  // Shared cookies path (in project root for both NestJS and Worker to access)
  private getSharedCookiesPath(): string {
    if (process.env.YOUTUBE_COOKIES_PATH) {
      return process.env.YOUTUBE_COOKIES_PATH;
    }

    // Resolve to project root regardless of where backend is executed from
    const cwd = process.cwd();
    const projectRoot = cwd.includes('apps' + path.sep + 'web-api')
      ? path.resolve(cwd, '..', '..')
      : cwd;

    return path.resolve(projectRoot, 'shared-cookies', 'youtube-cookies.txt');
  }

  private getSharedAlertPath(): string {
    if (process.env.YOUTUBE_ALERT_FLAG_PATH) {
      return process.env.YOUTUBE_ALERT_FLAG_PATH;
    }

    // Same logic as getSharedCookiesPath
    const cwd = process.cwd();
    const projectRoot = cwd.includes('apps' + path.sep + 'web-api')
      ? path.resolve(cwd, '..', '..')
      : cwd;

    return path.resolve(projectRoot, 'shared-cookies', 'youtube-auth-required.flag');
  }

  private get cookiesPath(): string {
    return this.getSharedCookiesPath();
  }

  private get alertFilePath(): string {
    return this.getSharedAlertPath();
  }

  /**
   * Get YouTube health status.
   */
  async getYouTubeStatus(): Promise<YouTubeStatusDto> {
    const cookiesFileExists = fs.existsSync(this.cookiesPath);
    const authAlertActive = fs.existsSync(this.alertFilePath);

    let validation: CookiesValidationDto = {
      isValid: false,
      fileExists: cookiesFileExists,
      cookieCount: 0,
      hasYouTubeDomain: false,
      message: 'No validation performed',
    };

    if (cookiesFileExists) {
      validation = await this.validateCookiesFile();
    }

    // For now, excluded sources will be fetched from logs or a persistent state file
    // In a full implementation, the worker would write this to a JSON file
    const excludedSources = await this.getExcludedYouTubeSources();

    const status = this.determineStatus(validation, authAlertActive, excludedSources.length > 0);

    return {
      status,
      cookiesFileExists,
      cookiesValid: validation.isValid,
      cookieCount: validation.cookieCount,
      earliestExpiration: validation.earliestExpiration,
      hasYouTubeDomain: validation.hasYouTubeDomain,
      excludedYouTubeSources: excludedSources,
      authAlertActive,
      alertFilePath: this.alertFilePath,
      message: validation.message,
      lastCheckTime: new Date().toISOString(),
      totalYouTubeSources: 0, // TODO: fetch from worker
      activeYouTubeSources: 0, // TODO: fetch from worker
    };
  }

  /**
   * Get cookies file content (for worker consumption via HTTP).
   */
  async getCookiesContent(): Promise<string | null> {
    try {
      if (!fs.existsSync(this.cookiesPath)) {
        return null;
      }
      const content = await fs.promises.readFile(this.cookiesPath, 'utf-8');
      return content;
    } catch (error) {
      this.logger.warn(`Failed to read cookies file: ${error}`);
      return null;
    }
  }

  /**
   * Save cookies file from uploaded content.
   */
  async saveCookies(cookiesContent: string): Promise<SaveCookiesResponseDto> {
    // Validate format
    const validation = await this.validateCookiesContent(cookiesContent);

    if (!validation.isValid) {
      throw new Error(validation.message || 'Invalid cookies format');
    }

    // Create directory if not exists
    const directory = path.dirname(this.cookiesPath);
    if (!fs.existsSync(directory)) {
      fs.mkdirSync(directory, { recursive: true });
    }

    // Write cookies file to shared location
    fs.writeFileSync(this.cookiesPath, cookiesContent, 'utf-8');
    this.logger.log(`Cookies file saved: ${this.cookiesPath}`);

    // Also sync cookies to worker's stage directory for fallback
    const workerCookiesPath = path.resolve(process.cwd(), 'apps/media-core-worker/stage/cookies/youtube-cookies.txt');
    try {
      fs.mkdirSync(path.dirname(workerCookiesPath), { recursive: true });
      fs.writeFileSync(workerCookiesPath, cookiesContent, 'utf-8');
      this.logger.log(`Cookies synced to worker fallback: ${workerCookiesPath}`);
    } catch (syncError) {
      this.logger.warn(
        `Failed to sync cookies to worker fallback: ${syncError instanceof Error ? syncError.message : String(syncError)}`,
      );
    }

    // Clear alert flags
    try {
      if (fs.existsSync(this.alertFilePath)) {
        fs.unlinkSync(this.alertFilePath);
        this.logger.log('YouTube auth alert flag cleared');
      }
      // Also clear worker's alert flag
      const workerAlertPath = path.resolve(process.cwd(), 'apps/media-core-worker/stage/cookies/youtube-auth-required.flag');
      if (fs.existsSync(workerAlertPath)) {
        fs.unlinkSync(workerAlertPath);
        this.logger.log('Worker YouTube auth alert flag cleared');
      }
    } catch (error) {
      this.logger.warn(
        `Failed to clear alert flag: ${error instanceof Error ? error.message : String(error)}`,
      );
    }

    // Re-validate to ensure it was written correctly
    const revalidation = await this.validateCookiesFile();

    return {
      success: revalidation.isValid,
      message: revalidation.isValid
        ? 'Cookies saved successfully'
        : `Cookies saved but validation failed: ${revalidation.message}`,
      cookiesPath: this.cookiesPath,
      validation: revalidation,
      timestamp: new Date().toISOString(),
    };
  }

  /**
   * Validate cookies content (Netscape format).
   */
  private async validateCookiesContent(
    content: string,
  ): Promise<CookiesValidationDto> {
    if (!content || content.trim().length === 0) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: 0,
        hasYouTubeDomain: false,
        message: 'Cookies content is empty',
      };
    }

    // Check for Netscape header
    const lines = content.split(/\r?\n/);
    const hasNetscapeHeader = lines.some((l) =>
      l.startsWith('# Netscape HTTP Cookie File'),
    );

    if (!hasNetscapeHeader) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: 0,
        hasYouTubeDomain: false,
        message: 'Missing Netscape HTTP Cookie File header',
      };
    }

    // Parse cookies
    const cookies: { domain: string; expiration: number; name: string }[] = [];
    let hasYouTubeDomain = false;

    for (const line of lines) {
      if (!line.trim() || line.startsWith('#')) continue;

      const parts = line.split('\t');
      if (parts.length < 7) continue;

      try {
        const domain = parts[0];
        const expiration = parseInt(parts[4], 10);
        const name = parts[5];

        cookies.push({ domain, expiration, name });

        if (domain.includes('youtube.com')) {
          hasYouTubeDomain = true;
        }
      } catch (error) {
        // Skip invalid cookie lines
      }
    }

    if (cookies.length === 0) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: 0,
        hasYouTubeDomain: false,
        message: 'No valid cookies found',
      };
    }

    if (!hasYouTubeDomain) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: cookies.length,
        hasYouTubeDomain: false,
        message: 'No cookies for youtube.com domain found',
      };
    }

    // Check expiration
    const earliestExp = new Date(Math.min(...cookies.map((c) => c.expiration * 1000)));
    const now = new Date();

    if (earliestExp < now) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: cookies.length,
        earliestExpiration: earliestExp.toISOString(),
        hasYouTubeDomain: true,
        message: `Cookies expired at ${earliestExp.toISOString()}`,
      };
    }

    return {
      isValid: true,
      fileExists: false,
      cookieCount: cookies.length,
      earliestExpiration: earliestExp.toISOString(),
      hasYouTubeDomain: true,
      message: `Valid: ${cookies.length} cookies, YouTube domain, expires ${earliestExp.toISOString()}`,
    };
  }

  /**
   * Validate existing cookies file.
   */
  private async validateCookiesFile(): Promise<CookiesValidationDto> {
    if (!fs.existsSync(this.cookiesPath)) {
      return {
        isValid: false,
        fileExists: false,
        cookieCount: 0,
        hasYouTubeDomain: false,
        message: 'Cookies file not found',
      };
    }

    try {
      const content = fs.readFileSync(this.cookiesPath, 'utf-8');
      return await this.validateCookiesContent(content);
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      return {
        isValid: false,
        fileExists: true,
        cookieCount: 0,
        hasYouTubeDomain: false,
        message: `Error reading cookies file: ${errorMsg}`,
      };
    }
  }

  /**
   * Get excluded YouTube sources (TODO: implement from worker state).
   */
  private async getExcludedYouTubeSources(): Promise<string[]> {
    // TODO: Read from a persistent state file written by the worker
    // For now, return empty array
    return [];
  }

  /**
   * Determine overall health status.
   */
  private determineStatus(
    validation: CookiesValidationDto,
    authAlertActive: boolean,
    hasExcluded: boolean,
  ): 'healthy' | 'degraded' | 'unhealthy' {
    if (authAlertActive) return 'unhealthy';
    if (!validation.isValid) return 'degraded';
    if (hasExcluded) return 'degraded';
    return 'healthy';
  }

  /**
   * Start a manual login session in a real browser window.
   * Uses a dedicated temporary profile to avoid interfering with Edge already running.
   */
  async startManualLoginSession(): Promise<StartYouTubeLoginSessionResponseDto> {
    this.cleanupExpiredSessions();
    await this.closeAllSessions();

    const sessionId = crypto.randomUUID();
    const createdAt = Date.now();
    const expiresAt = new Date(createdAt + this.sessionTtlMs).toISOString();

    let browser: Browser | null = null;

    try {
      // Always use a temporary profile to avoid interfering with user's Edge
      const tempProfileDir = path.join(
        process.env.TEMP || process.env.TMP || 'C:\\Windows\\Temp',
        'puppeteer-youtube-session-' + Date.now(),
      );
      browser = await this.launchEdgeWithProfile(tempProfileDir, 'PuppeteerProfile');

      const existingPages = await browser.pages();
      let page = existingPages.length > 0 ? existingPages[0] : await browser.newPage();
      page.setDefaultTimeout(30000);

      // Close all other pages to keep only one window
      for (let i = 1; i < existingPages.length; i++) {
        try {
          await existingPages[i].close();
        } catch {
          // Ignore close errors
        }
      }

      // Always navigate to YouTube to ensure we're on the right page
      await page.goto('https://www.youtube.com', {
        waitUntil: 'domcontentloaded',
      });

      await page.bringToFront();

      this.loginSessions.set(sessionId, { browser, page, createdAt });
      this.latestSessionId = sessionId;
      this.logger.log(`[YouTubeService] Login session started: ${sessionId}`);

      return {
        success: true,
        message:
          'Browser opened. If you are already signed in, click obtain cookies directly; otherwise sign in there and then obtain cookies.',
        sessionId,
        expiresAt,
        timestamp: new Date().toISOString(),
      };
    } catch (error) {
      if (browser) {
        await browser.close().catch(() => {
          // Ignore close errors
        });
      }
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`[YouTubeService] Failed to start manual login session: ${errorMsg}`);
      return {
        success: false,
        message: `Failed to open browser: ${errorMsg}`,
        timestamp: new Date().toISOString(),
      };
    }
  }

  private async launchEdgeWithProfile(
    userDataDir: string,
    profileName: string,
  ): Promise<Browser> {
    return puppeteer.launch({
      headless: false,
      executablePath: this.browserExecutablePath,
      userDataDir,
      ignoreDefaultArgs: ['--enable-automation'],
      args: [
        `--profile-directory=${profileName}`,
      ],
    });
  }

  /**
   * Extract and save cookies from an active manual login session.
   */
  async extractCookiesFromSession(sessionId?: string): Promise<ExtractCookiesResponseDto> {
    this.cleanupExpiredSessions();

    const effectiveSessionId =
      sessionId && sessionId.trim().length > 0 ? sessionId : this.latestSessionId;

    if (!effectiveSessionId) {
      return {
        success: false,
        message: 'No active session found. Start a new login session.',
        timestamp: new Date().toISOString(),
      };
    }

    const session = this.loginSessions.get(effectiveSessionId);
    if (!session) {
      return {
        success: false,
        message: 'Session not found or expired. Start a new login session.',
        timestamp: new Date().toISOString(),
      };
    }

    try {
      await session.page.goto('https://www.youtube.com', { waitUntil: 'networkidle2' });
      const cookies = await session.page.cookies();
      const relevantCookies = cookies.filter(
        (cookie) =>
          cookie.domain?.includes('youtube.com') ||
          cookie.domain?.includes('google.com') ||
          cookie.domain?.includes('googlevideo.com'),
      );

      if (relevantCookies.length === 0) {
        return {
          success: false,
          message: 'No YouTube/Google cookies found yet. Make sure you are logged in and retry.',
          timestamp: new Date().toISOString(),
        };
      }

      const netscapeContent = this.convertToNetscapeFormat(relevantCookies);
      await fs.promises.mkdir(path.dirname(this.cookiesPath), { recursive: true });
      await fs.promises.writeFile(this.cookiesPath, netscapeContent, 'utf-8');

      // Also sync cookies to worker's local stage directory for fallback
      const workerCookiesPath = path.resolve(process.cwd(), 'apps/media-core-worker/stage/cookies/youtube-cookies.txt');
      try {
        await fs.promises.mkdir(path.dirname(workerCookiesPath), { recursive: true });
        await fs.promises.writeFile(workerCookiesPath, netscapeContent, 'utf-8');
        this.logger.log(`[YouTubeService] Cookies synced to worker: ${workerCookiesPath}`);
      } catch (syncError) {
        this.logger.warn(
          `[YouTubeService] Failed to sync cookies to worker fallback: ${syncError instanceof Error ? syncError.message : String(syncError)}`,
        );
      }

      const validation = await this.validateCookiesFile();
      await this.clearAuthAlert();

      return {
        success: validation.isValid,
        message: validation.isValid
          ? `Cookies extracted and saved (${relevantCookies.length} cookies)`
          : `Cookies extracted but validation failed: ${validation.message}`,
        cookiesPath: this.cookiesPath,
        validation,
        timestamp: new Date().toISOString(),
        extractedCookieCount: relevantCookies.length,
      };
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.error(`[YouTubeService] Failed to extract cookies from session: ${errorMsg}`);
      return {
        success: false,
        message: `Failed to extract cookies: ${errorMsg}`,
        timestamp: new Date().toISOString(),
      };
    } finally {
      await this.closeSession(effectiveSessionId);
    }
  }

  /**
   * Cancel a manual login session and close browser window.
   */
  async cancelManualLoginSession(sessionId?: string): Promise<void> {
    const effectiveSessionId =
      sessionId && sessionId.trim().length > 0 ? sessionId : this.latestSessionId;

    if (!effectiveSessionId) {
      return;
    }

    await this.closeSession(effectiveSessionId);
  }

  /**
   * Convert cookies to Netscape format.
   */
  private convertToNetscapeFormat(cookies: any[]): string {
    const lines = [
      '# Netscape HTTP Cookie File',
      '# This file was generated by Radio Alert',
      '# Do not edit this file in a text editor',
    ];

    for (const cookie of cookies) {
      const httpOnly = cookie.httpOnly ? 'TRUE' : 'FALSE';
      const secure = cookie.secure ? 'TRUE' : 'FALSE';
      // For session cookies without explicit expiration, use year 2099
      const expiration = cookie.expires && cookie.expires > 0 ? Math.floor(cookie.expires) : 4070908800;

      lines.push(
        [
          cookie.domain || '',
          'TRUE',
          cookie.path || '/',
          secure,
          expiration,
          cookie.name,
          cookie.value,
        ].join('\t'),
      );
    }

    return lines.join('\n');
  }

  /**
   * Clear authentication alert flag.
   */
  private async clearAuthAlert(): Promise<void> {
    try {
      if (fs.existsSync(this.alertFilePath)) {
        await fs.promises.unlink(this.alertFilePath);
        this.logger.debug('[YouTubeService] Alert flag cleared');
      }
      // Also clear worker's alert flag
      const workerAlertPath = path.resolve(process.cwd(), 'apps/media-core-worker/stage/cookies/youtube-auth-required.flag');
      if (fs.existsSync(workerAlertPath)) {
        await fs.promises.unlink(workerAlertPath);
        this.logger.debug('[YouTubeService] Worker alert flag cleared');
      }
    } catch (error) {
      this.logger.warn(`[YouTubeService] Could not clear alert flag: ${error}`);
    }
  }

  private cleanupExpiredSessions(): void {
    const now = Date.now();
    for (const [sessionId, session] of this.loginSessions.entries()) {
      if (now - session.createdAt > this.sessionTtlMs) {
        this.closeSession(sessionId).catch((error) => {
          this.logger.warn(
            `[YouTubeService] Failed to close expired session ${sessionId}: ${String(error)}`,
          );
        });
      }
    }
  }

  private async closeSession(sessionId: string): Promise<void> {
    const session = this.loginSessions.get(sessionId);
    if (!session) {
      return;
    }

    this.loginSessions.delete(sessionId);
    if (this.latestSessionId === sessionId) {
      this.latestSessionId = this.loginSessions.keys().next().value || null;
    }
    try {
      await session.page.close();
    } catch {
      // Ignore close errors during cleanup.
    }
    try {
      await session.browser.close();
    } catch {
      // Ignore close errors during cleanup.
    }
  }

  private async closeAllSessions(): Promise<void> {
    const sessionIds = Array.from(this.loginSessions.keys());
    for (const sessionId of sessionIds) {
      await this.closeSession(sessionId);
    }
  }

  /**
   * Send current cookies to the .NET worker via HTTP endpoint.
   */
  async sendCookiesToWorker(): Promise<{ success: boolean; message: string }> {
    try {
      const cookies = await this.getCookiesContent();
      if (!cookies) {
        return {
          success: false,
          message: 'No cookies found. Extract cookies first.',
        };
      }

      // Try to send to worker (default localhost:5000, but can be configured)
      const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';
      const endpoint = `${workerUrl}/youtube/cookies`;

      this.logger.log(`[YouTubeService] Sending cookies to worker at: ${endpoint}`);

      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cookies }),
        signal: AbortSignal.timeout(10000),
      });

      const data = await response.json();

      if (data?.success) {
        this.logger.log('[YouTubeService] Cookies synced to worker successfully');
        return {
          success: true,
          message: 'Cookies sent to worker successfully',
        };
      }

      return {
        success: false,
        message: data?.message || 'Worker rejected cookies',
      };
    } catch (error) {
      const errorMsg = error instanceof Error ? error.message : String(error);
      this.logger.warn(`[YouTubeService] Failed to send cookies to worker: ${errorMsg}`);

      // Return partial success - cookies are still saved locally
      return {
        success: false,
        message: `Could not reach worker: ${errorMsg}. Cookies are saved locally.`,
      };
    }
  }
}
