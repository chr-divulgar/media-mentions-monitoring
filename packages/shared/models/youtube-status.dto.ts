export interface YouTubeStatusDto {
  status: 'healthy' | 'degraded' | 'unhealthy' | 'worker_unreachable';
  workerReachable: boolean;
  cookiesFileExists: boolean | null;
  cookiesValid: boolean | null;
  cookieCount: number | null;
  earliestExpiration: string | null;
  hasYouTubeDomain: boolean | null;
  excludedYouTubeSources: string[];
  authAlertActive: boolean;
  alertFilePath: string;
  message: string;
  lastCheckTime: string;
  totalYouTubeSources: number | null;
  activeYouTubeSources: number | null;
}

export interface CookiesValidationDto {
  isValid: boolean;
  fileExists: boolean;
  cookieCount?: number;
  earliestExpiration?: string;
  hasYouTubeDomain: boolean;
  message: string;
}

export interface SaveCookiesDto {
  cookiesContent: string;
}

export interface SaveCookiesResponseDto {
  success: boolean;
  message: string;
  cookiesPath: string;
  validation: CookiesValidationDto;
  timestamp: string;
}

export interface StartYouTubeLoginSessionResponseDto {
  success: boolean;
  message: string;
  sessionId?: string;
  expiresAt?: string;
  timestamp: string;
}

export interface ExtractCookiesDto {
  sessionId?: string;
}

export interface ExtractCookiesResponseDto {
  success: boolean;
  message: string;
  cookiesPath?: string;
  validation?: CookiesValidationDto;
  timestamp: string;
  extractedCookieCount?: number;
}

export interface AuthInstructionsDto {
  youtubeUrl: string;
  extensionUrl: string;
  steps: AuthStep[];
  alternativeExtensions: ExtensionInfo[];
  format: string;
}

export interface AuthStep {
  step: number;
  action: string;
  url?: string;
}

export interface ExtensionInfo {
  name: string;
  browsers: string;
  url: string;
}
