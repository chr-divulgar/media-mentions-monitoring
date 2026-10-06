import type { AlertDto } from "@repo/shared/index";

// TV alerts from the worker have a recorded MP4 to edit; everything else keeps the audio flow.
export const isTvAlert = (alert?: AlertDto | null): boolean =>
  alert?.source === "worker" &&
  ["tv", "television"].includes((alert.media ?? "").toLowerCase());

// Bogotá calendar day (UTC-5), the day the capture folders are organised by.
const bogotaDay = (date: Date): string =>
  new Date(date.getTime() - 5 * 60 * 60 * 1000).toISOString().slice(0, 10);

// An alert from today still has its recording on this machine; an earlier one is only on the
// archive server, so the editor treats a missing video differently for each.
export const isFromToday = (alert?: AlertDto | null): boolean =>
  !!alert?.endTime && bogotaDay(new Date(alert.endTime)) === bogotaDay(new Date());
