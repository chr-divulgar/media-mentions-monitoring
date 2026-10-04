import {
  CanActivate,
  ExecutionContext,
  ForbiddenException,
  Injectable,
  Logger,
  UnauthorizedException,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { AuthService } from './auth.service';
import { IS_PUBLIC_KEY, ROLES_KEY } from './auth.decorators';

type AuthedRequest = {
  headers: Record<string, string | string[] | undefined>;
  query?: Record<string, string | string[] | undefined>;
  method?: string;
  url?: string;
  user?: { uid: string; role: string };
};

/**
 * Global guard: every route needs a valid Firebase ID token and an approved role (RQ-041).
 *
 * Rollout switch: with AUTH_ENFORCE unset (the default) the guard only LOGS what it would have
 * rejected and lets the request through, so it can be switched on after confirming from the logs
 * that the portal sends tokens everywhere. Set AUTH_ENFORCE=true to start rejecting (401/403).
 */
@Injectable()
export class AuthGuard implements CanActivate {
  private readonly logger = new Logger(AuthGuard.name);
  private readonly warned = new Set<string>();

  constructor(
    private readonly reflector: Reflector,
    private readonly authService: AuthService,
  ) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    // The status gateway (socket.io) is not an HTTP route; it is out of scope for this guard.
    if (context.getType() !== 'http') return true;

    const targets = [context.getHandler(), context.getClass()];
    if (this.reflector.getAllAndOverride<boolean>(IS_PUBLIC_KEY, targets)) {
      return true;
    }

    const request = context.switchToHttp().getRequest<AuthedRequest>();
    const enforce = process.env.AUTH_ENFORCE === 'true';

    try {
      const token = this.extractToken(request);
      if (!token) throw new UnauthorizedException('Se requiere token de autenticación');

      const user = await this.authService.authenticate(token);
      request.user = user;

      const required = this.reflector.getAllAndOverride<string[] | undefined>(ROLES_KEY, targets);
      const allowed = required?.length ? required.includes(user.role) : user.role !== 'initial';
      if (!allowed) {
        throw new ForbiddenException('No tienes permisos para esta acción');
      }
      return true;
    } catch (error) {
      if (enforce) throw error;

      const key = `${request.method} ${(request.url ?? '').split('?')[0]}`;
      if (!this.warned.has(key)) {
        this.warned.add(key);
        this.logger.warn(
          `[audit mode] would reject ${key}: ${error instanceof Error ? error.message : JSON.stringify(error)}`,
        );
      }
      return true;
    }
  }

  // Browsers cannot attach an Authorization header to <audio> / waveform URLs, so the token may
  // also arrive as ?access_token=.
  private extractToken(request: AuthedRequest): string | null {
    const header = request.headers['authorization'];
    const value = Array.isArray(header) ? header[0] : header;
    if (value?.startsWith('Bearer ')) return value.slice('Bearer '.length);

    const query = request.query?.['access_token'];
    return (Array.isArray(query) ? query[0] : query) ?? null;
  }
}
