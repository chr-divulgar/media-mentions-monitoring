// Media helpers shared by every place that builds a note: the manual note form and the editors
// opened from an alert (radio, television and internet).

export const normalizeMedia = (value?: string): string =>
  (value ?? "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "");

// Radio and television notes carry a clip (and a program slot); every other medium is a web or
// print note with a PDF. Media is stored as "tv" as well as "television".
export const isRadioOrTelevisionMedia = (media?: string): boolean => {
  const normalized = normalizeMedia(media);
  return (
    normalized.includes("radio") ||
    normalized.includes("television") ||
    normalized.includes("tv")
  );
};

// The file a note of this medium attaches: the audio, the video, or the page's PDF.
export const expectedExtension = (media?: string): "mp3" | "mp4" | "pdf" => {
  const normalized = normalizeMedia(media);
  if (normalized.includes("radio")) return "mp3";
  if (normalized.includes("television") || normalized === "tv") return "mp4";
  return "pdf";
};

export const acceptByMedia = (media?: string): string => {
  const extension = expectedExtension(media);
  if (extension === "mp3") return ".mp3,audio/mpeg";
  if (extension === "mp4") return ".mp4,video/mp4";
  return ".pdf,application/pdf";
};

export const isAttachmentAllowed = (fileName: string, media?: string): boolean =>
  fileName.toLowerCase().endsWith(`.${expectedExtension(media)}`);
