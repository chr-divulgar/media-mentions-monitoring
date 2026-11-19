import moment from "moment";
import { AlertDto } from "./alerts.dto";

/** Each click of the editor's "+10 min" buttons adds this much audio to one side of the window. */
export const SEGMENT_EXTENSION_STEP_SECONDS = 600;

/** The longest window the editor can ask for, the 30 min / 1 h / 2 h one together with its extensions. */
export const MAX_SEGMENT_WINDOW_SECONDS = 4 * 3600;

export class CreateFileDto {
  readonly alert?: AlertDto | null;
  readonly startSecond?: number;
  readonly output?: string;
  readonly duration?: number;
  /**
   * Segment only: seconds of audio before and after the end of the alert, when the editor extended the window with
   * its "+10 min" buttons. Without them the window is centred on the end of the alert and spans `duration`.
   */
  readonly before?: number;
  readonly after?: number;
}

export interface FileDto {
  readonly startSeconds: number;
  readonly duration: number;
}

export class Fragment {
  readonly startTime?: Date;
  readonly duration?: moment.Duration;
}

export interface AudioFile {
  startSeconds: number;
  duration: number;
}
