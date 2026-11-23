/** A client's abbreviation as it goes in a file name: letters and digits, upper case, no accents, at most ten. */
export function cleanAbbreviation(value?: string | null): string {
  return (value ?? "")
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, "")
    .slice(0, 10);
}

/** The abbreviation the client has, or else the first four letters of its name (Ecopetrol → ECOP). */
export function clientAbbreviation(client: { name?: string; abbreviation?: string }): string {
  return cleanAbbreviation(client.abbreviation) || cleanAbbreviation(client.name).slice(0, 4);
}

/**
 * The name a radio or TV note's audio is filed under: NOTA_{number}_{program}_{dd-MM-yy}_{client}.{extension}, for
 * instance NOTA_01_JAVERIANA_ESTEREO_AM_19-11-26_ECOP.mp3. The program label of a slot comes with underscores at both
 * ends (_JAVERIANA_ESTEREO_AM_), which are trimmed so none is repeated or left hanging. A date or client that is not
 * known is left out instead of leaving a gap. Without a program slot there is no name yet.
 *
 * `date` is the note's day as YYYY-MM-DD.
 */
export function composeNoteAudioLabel(input: {
  index?: string;
  slotLabel?: string;
  date?: string;
  abbreviation?: string;
  extension: string;
}): string | undefined {
  const program = (input.slotLabel ?? "").replace(/^_+|_+$/g, "");
  if (!program) return undefined;

  const parts = ["NOTA", input.index?.trim() || "##", program];
  const day = /^(\d{4})-(\d{2})-(\d{2})/.exec(input.date ?? "");
  if (day) parts.push(`${day[3]}-${day[2]}-${day[1].slice(2)}`);
  const abbreviation = cleanAbbreviation(input.abbreviation);
  if (abbreviation) parts.push(abbreviation);

  return `${parts.join("_")}.${input.extension}`;
}
