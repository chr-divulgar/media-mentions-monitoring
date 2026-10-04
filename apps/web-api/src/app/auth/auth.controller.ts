import { Controller, Get, Headers, UnauthorizedException } from '@nestjs/common';
import { AuthService } from './auth.service';
import { Public } from './auth.decorators';

@Controller('auth')
export class AuthController {
  constructor(private readonly authService: AuthService) {}

  // Verifies the token itself (it is how a new user gets their profile), so it skips the global guard.
  @Public()
  @Get('profile')
  async getProfile(@Headers('authorization') authorization: string) {
    if (!authorization?.startsWith('Bearer ')) {
      throw new UnauthorizedException('Se requiere token de autenticación');
    }
    const token = authorization.replace('Bearer ', '');
    return this.authService.getProfile(token);
  }
}
