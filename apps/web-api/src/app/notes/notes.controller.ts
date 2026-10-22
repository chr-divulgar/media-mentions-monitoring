import {
  Controller,
  Post,
  Body,
  Get,
  Param,
  Query,
  StreamableFile,
  HttpException,
  HttpStatus,
} from '@nestjs/common';
import { NotesService } from './notes.service';
import {
  NoteDto,
  DashboardDataDto,
  DashboardPeriod,
  CreateInternetPdfDto,
  SaveInternetNoteDto,
} from '@repo/shared';

@Controller('notes')
export class NotesController {
  constructor(private readonly notesService: NotesService) {}

  @Post('set-note')
  async setNote(@Body() noteDto: NoteDto) {
    try {
      return await this.notesService.setNote(noteDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request setNote ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  // The three internet-note routes answer with user-facing messages (from the worker or from the
  // checks in NotesService), so the error text is passed through as is.
  @Post('internet-pdf/preview')
  async previewInternetNote(@Body() dto: CreateInternetPdfDto) {
    try {
      return await this.notesService.previewInternetNote(dto.alertId);
    } catch (error) {
      throw this.internetNoteError(error);
    }
  }

  // A PDF the user made by hand when the page could not be printed: the body is the file itself.
  @Post('internet-pdf/upload')
  async uploadInternetNotePdf(
    @Query('alertId') alertId: string,
    @Body() pdf: Buffer,
  ) {
    try {
      return await this.notesService.uploadInternetNotePdf(alertId, pdf);
    } catch (error) {
      throw this.internetNoteError(error);
    }
  }

  @Get('internet-pdf/preview/:previewId')
  async getInternetPreviewPdf(@Param('previewId') previewId: string) {
    try {
      return new StreamableFile(
        await this.notesService.getInternetPreviewPdf(previewId),
        { type: 'application/pdf' },
      );
    } catch (error) {
      throw this.internetNoteError(error);
    }
  }

  @Post('internet-pdf/save')
  async saveInternetNote(@Body() dto: SaveInternetNoteDto) {
    try {
      return await this.notesService.saveInternetNote(dto);
    } catch (error) {
      throw this.internetNoteError(error);
    }
  }

  private internetNoteError(error: unknown): HttpException {
    return new HttpException(
      {
        status: HttpStatus.BAD_GATEWAY,
        error: error instanceof Error ? error.message : String(error),
      },
      HttpStatus.BAD_GATEWAY,
    );
  }

  @Post('send-note')
  async sendNote(@Body() noteDto: NoteDto) {
    try {
      return await this.notesService.sendMessage(noteDto);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request seendNote ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('import-excel')
  async importExcel(@Body() notes: NoteDto[]) {
    try {
      return await this.notesService.importNotes(notes);
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request importExcel ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('dates')
  async getDates() {
    try {
      return await this.notesService.getMinMaxDates();
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getDates ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('list')
  async listNotes(@Body() body: { startDate: string; endDate: string }) {
    try {
      return await this.notesService.listNotesByDateRange(
        body.startDate,
        body.endDate,
      );
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request listNotes ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }

  @Post('dashboard')
  async getDashboardData(
    @Body()
    body: {
      startDate: string;
      endDate: string;
      period?: DashboardPeriod;
    },
  ): Promise<DashboardDataDto> {
    try {
      return await this.notesService.getDashboardData(
        body.startDate,
        body.endDate,
        body.period,
      );
    } catch (error) {
      throw new HttpException(
        {
          status: HttpStatus.INTERNAL_SERVER_ERROR,
          error: `There was an error processing the request getDashboardData ${error}`,
        },
        HttpStatus.INTERNAL_SERVER_ERROR,
      );
    }
  }
}
