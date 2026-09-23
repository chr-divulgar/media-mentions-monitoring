import * as path from "path";

export function getDateFromFile(filePath: string): Date {
  const fileName = path.basename(filePath);
  const parts = fileName.split("_");
  if (parts.length < 3) throw new Error("Invalid file name format.");
  // Date and time are always the last two "_"-separated segments before the extension
  // (<prefix>_<yyyy-MM-dd>_<HH-mm-ss>.ext) — read from the end, not fixed indices 1/2, since the
  // prefix itself is a sourceId that routinely contains underscores (e.g. "Radio_Uno_Villavicencio",
  // "La_popular_Estereo"). audio.service.ts's resolveSourceFiles already parses this correctly the
  // same way; this was the one place still assuming a single-word prefix.
  const datePart = parts[parts.length - 2];
  const timePart = parts[parts.length - 1].split(".")[0];
  return new Date(`${datePart}T${timePart.replace(/-/g, ":")}.000-05:00`);
}
