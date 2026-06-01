import { Controller, Get, Headers, UnauthorizedException } from '@nestjs/common';
import { AuthService } from './auth.service';

@Controller('auth')
export class AuthController {
  constructor(private readonly authService: AuthService) {}

  @Get('profile')
  async getProfile(@Headers('authorization') authorization: string) {
    if (!authorization?.startsWith('Bearer ')) {
      throw new UnauthorizedException('Se requiere token de autenticación');
    }
    const token = authorization.replace('Bearer ', '');
    return this.authService.getProfile(token);
  }
}
