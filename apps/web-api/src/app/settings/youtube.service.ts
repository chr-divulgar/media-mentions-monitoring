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

// Mirrors the worker's YouTubeHealthResponse (GET /youtube/health), serialized camelCase.
type WorkerHealthPayload = {
  authAlertActive: boolean;
  cookiesFileExists: boolean;
  cookiesValid: boolean;
  cookieCount?: number;
  earliestExpiration?: string;
  hasYouTubeDomain: boolean;
  excludedSourceIds: string[];
  totalTvSources: number;
  activeTvSources: number;
  message: string;
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

  // Shared cookies directory (project root, readable by both NestJS and the worker).
  // Resolved from this file's own location rather than process.cwd() — the cwd varies
  // depending on how NestJS was launched (from the repo root vs. from apps/web-api),
  // which previously caused the cookies file and alert flag to be written to different,
  // inconsistent directories depending on how the process happened to start.
  // __dirname is apps/web-api/src/app/settings (or dist/app/settings once compiled),
  // which sits at the same depth under apps/web-api either way.
  private static readonly PROJECT_ROOT = path.resolve(__dirname, '..', '..', '..', '..', '..');

  private getSharedCookiesPath(): string {
    return (
      process.env.YOUTUBE_COOKIES_PATH ||
      path.resolve(YouTubeService.PROJECT_ROOT, 'shared-cookies', 'youtube-cookies.txt')
    );
  }

  private getSharedAlertPath(): string {
    return (
      process.env.YOUTUBE_ALERT_FLAG_PATH ||
      path.resolve(YouTubeService.PROJECT_ROOT, 'shared-cookies', 'youtube-auth-required.flag')
    );
  }

  private get cookiesPath(): string {
    return this.getSharedCookiesPath();
  }

  private get alertFilePath(): string {
    return this.getSharedAlertPath();
  }

  /**
   * Get YouTube health status. The worker is the single source of truth: if it can't be
   * reached, the status is 'worker_unreachable' — there is no local-filesystem fallback,
   * since a stale cookies file on disk says nothing about whether the worker that actually
   * records is alive.
   */
  async getYouTubeStatus(): Promise<YouTubeStatusDto> {
    const health = await this.checkWorkerHealth();

    if (!health.reachable || !health.data) {
      return {
        status: 'worker_unreachable',
        workerReachable: false,
        cookiesFileExists: null,
        cookiesValid: null,
        cookieCount: null,
        earliestExpiration: null,
        hasYouTubeDomain: null,
        excludedYouTubeSources: [],
        authAlertActive: false,
        alertFilePath: this.alertFilePath,
        message: 'Worker is unreachable — cannot verify cookie or recording status.',
        lastCheckTime: new Date().toISOString(),
        totalYouTubeSources: null,
        activeYouTubeSources: null,
      };
    }

    const { data } = health;
    const status = this.determineStatus({
      authAlertActive: data.authAlertActive,
      cookiesValid: data.cookiesValid,
      hasExcluded: data.excludedSourceIds.length > 0,
    });

    return {
      status,
      workerReachable: true,
      cookiesFileExists: data.cookiesFileExists,
      cookiesValid: data.cookiesValid,
      cookieCount: data.cookieCount ?? null,
      earliestExpiration: data.earliestExpiration ?? null,
      hasYouTubeDomain: data.hasYouTubeDomain,
      excludedYouTubeSources: data.excludedSourceIds,
      authAlertActive: data.authAlertActive,
      alertFilePath: this.alertFilePath,
      message: data.message,
      lastCheckTime: new Date().toISOString(),
      totalYouTubeSources: data.totalTvSources,
      activeYouTubeSources: data.activeTvSources,
    };
  }

  /**
   * Check whether the worker's YouTube HTTP endpoint is reachable and, if so, fetch its
   * health snapshot (auth alert, cookie validity, excluded/active TV source counts).
   */
  private async checkWorkerHealth(): Promise<{ reachable: boolean; data?: WorkerHealthPayload }> {
    const workerUrl = process.env.YOUTUBE_WORKER_ENDPOINT || 'http://localhost:5000';

    try {
      const response = await fetch(`${workerUrl}/youtube/health`, {
        signal: AbortSignal.timeout(3000),
      });

      if (!response.ok) {
        return { reachable: false };
      }

      const data = (await response.json()) as WorkerHealthPayload;
      return { reachable: true, data };
    } catch (error) {
      this.logger.warn(
        `[YouTubeService] Worker health check failed: ${error instanceof Error ? error.message : String(error)}`,
      );
      return { reachable: false };
    }
  }

  /**
   * Get cookies file content. Used internally to relay the current cookies to the worker
   * over HTTP (see sendCookiesToWorker) — not exposed as a route since nothing else reads it.
   */
  private async getCookiesContent(): Promise<string | null> {
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

    // Write cookies file to the shared location the worker reads from.
    fs.writeFileSync(this.cookiesPath, cookiesContent, 'utf-8');
    this.logger.log(`Cookies file saved: ${this.cookiesPath}`);

    await this.clearAuthAlert();

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
   * Determine overall health status from the worker's health snapshot. Only called once the
   * worker has already been confirmed reachable — 'worker_unreachable' is decided earlier,
   * in getYouTubeStatus, before this ever runs.
   */
  private determineStatus(params: {
    authAlertActive: boolean;
    cookiesValid: boolean;
    hasExcluded: boolean;
  }): 'healthy' | 'degraded' | 'unhealthy' {
    if (params.authAlertActive) return 'unhealthy';
    if (!params.cookiesValid) return 'degraded';
    if (params.hasExcluded) return 'degraded';
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
    } catch (error) {
      this.logger.warn(
        `[YouTubeService] Could not clear alert flag: ${error instanceof Error ? error.message : String(error)}`,
      );
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
          // Forward the worker's own message rather than asserting a generic one here —
          // it's the worker that knows whether recovery is immediate or still pending.
          message: data.message || 'Cookies sent to worker successfully',
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
