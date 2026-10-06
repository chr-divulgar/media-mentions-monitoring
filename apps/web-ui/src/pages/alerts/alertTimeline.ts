import {
  AlertDto,
  CreateFileDto,
  FileDto,
  Fragment,
  getDateFromFile,
} from "@repo/shared/index";
import moment from "moment";

// Where the alert sits inside the current segment, in seconds from the segment's start.
// Worker alerts store real UTC timestamps; legacy alerts store local (Bogotá) wall-clock time
// mislabeled as UTC, so the 'Z' has to be swapped for the real offset.
export const alertWindowInSegment = (
  alert: AlertDto,
  segmentData: FileDto,
): { startSecond: number; endSecond: number } => {
  const fileTime = getDateFromFile(alert.filePath ?? "");
  const isWorker = alert.source === "worker";
  const toDate = (value?: string) =>
    new Date(isWorker ? (value ?? "") : (value?.replace("Z", "-05:00") ?? ""));

  return {
    startSecond:
      (toDate(alert.startTime).getTime() - fileTime.getTime()) / 1000 -
      segmentData.startSeconds,
    endSecond:
      (toDate(alert.endTime).getTime() - fileTime.getTime()) / 1000 -
      segmentData.startSeconds,
  };
};

// The same window as percentages of the segment, for the marker under the player.
export const alertMarkerPositions = (alert: AlertDto, segmentData: FileDto) => {
  const { startSecond, endSecond } = alertWindowInSegment(alert, segmentData);
  return {
    startPosition: (startSecond * 100) / segmentData.duration,
    endPosition: (endSecond * 100) / segmentData.duration,
  };
};

// What the editor hands up to the modal when the user selects [start, end] seconds of the segment:
// the fragment (shown in the footer and saved on the note) and the request that creates its file.
export const selectionToFragment = (
  alert: AlertDto | null | undefined,
  segmentData: FileDto,
  start: number,
  end: number,
): { fragment: Fragment; createFileDto: Partial<CreateFileDto> } => {
  const fileTime = getDateFromFile(alert?.filePath ?? "");
  return {
    fragment: {
      startTime: new Date(
        fileTime.getTime() + (segmentData.startSeconds + start) * 1000,
      ),
      duration: moment.duration(end - start, "seconds"),
    },
    createFileDto: {
      alert,
      startSecond: segmentData.startSeconds + start,
      output: `fragment_${alert?.id}`,
      duration: end - start,
    },
  };
};
