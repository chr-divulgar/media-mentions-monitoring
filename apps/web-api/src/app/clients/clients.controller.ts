import {
  Controller,
  Get,
  Post,
  Put,
  Delete,
  Body,
  Param,
  HttpException,
  HttpStatus,
} from '@nestjs/common';
import { ClientsService } from './clients.service';
import { ClientDto, MediaTypeDto } from '@repo/shared';

@Controller('clients')
export class ClientsController {
  constructor(private readonly clientsService: ClientsService) {}

  // ─── Clients ──────────────────────────────────────────────────────────────

  @Get()
  async getClients() {
    try {
      return await this.clientsService.getClients();
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Get(':id')
  async getClient(@Param('id') id: string) {
    try {
      return await this.clientsService.getClient(id);
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post()
  async createClient(@Body() dto: ClientDto) {
    try {
      return await this.clientsService.createClient(dto);
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Put(':id')
  async updateClient(@Param('id') id: string, @Body() dto: ClientDto) {
    try {
      return await this.clientsService.updateClient(id, dto);
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Delete(':id')
  async deleteClient(@Param('id') id: string) {
    try {
      await this.clientsService.deleteClient(id);
      return { success: true };
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  // ─── Media Types ──────────────────────────────────────────────────────────

  @Get('media/all')
  async getMediaTypes() {
    try {
      return await this.clientsService.getMediaTypes();
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('media')
  async createMediaType(@Body() dto: MediaTypeDto) {
    try {
      return await this.clientsService.createMediaType(dto);
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Put('media/:id')
  async updateMediaType(@Param('id') id: string, @Body() dto: MediaTypeDto) {
    try {
      return await this.clientsService.updateMediaType(id, dto);
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Delete('media/:id')
  async deleteMediaType(@Param('id') id: string) {
    try {
      await this.clientsService.deleteMediaType(id);
      return { success: true };
    } catch (error) {
      throw new HttpException(
        { status: HttpStatus.INTERNAL_SERVER_ERROR, error: String(error) },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
