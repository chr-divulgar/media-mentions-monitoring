/** Body of POST /notes/internet-pdf/preview: the internet alert to edit as a note. */
export class CreateInternetPdfDto {
  readonly alertId!: string;
}

/** Draft of an internet note and the PDF the worker printed for it; nothing is archived yet. */
export class InternetNotePreviewDto {
  readonly previewId!: string;
  /** Public link the PDF will have once the note is saved. */
  readonly pdfLink!: string;
  readonly note!: NoteDto;
}

/** Body of POST /notes/internet-pdf/save: the edited fields plus the preview to archive. */
export class SaveInternetNoteDto {
  readonly alertId!: string;
  readonly previewId!: string;
  readonly index?: string;
  readonly program?: string;
  readonly title!: string;
  readonly summary?: string;
  readonly topic?: string;
  readonly subtopic?: string;
  readonly subsubtopic?: string;
  readonly origin?: string;
  readonly sentiment?: string;
  readonly zone?: string;
  readonly city?: string;
  readonly rate?: string;
  readonly audience?: string;
  readonly value?: string;
}

export class NoteDto {
  readonly id?: string;
  readonly text?: string;
  readonly summary?: string;
  readonly title?: string;
  readonly index?: string;
  readonly program?: string;
  readonly media?: string;
  readonly mediaName?: string;
  readonly clientName?: string;
  readonly startTime?: string;
  readonly duration?: number;
  readonly audioLabel?: string;
  readonly alert_id?: string;
  readonly date?: string;
  readonly topic?: string;
  readonly subtopic?: string;
  readonly subsubtopic?: string;
  readonly origin?: string;
  readonly zone?: string;
  readonly city?: string;
  readonly rate?: string;
  readonly sentiment?: string;
  readonly value?: string;
  readonly audience?: string;
  readonly link?: string;
  readonly attachment?: string;
  message?: string;
}
