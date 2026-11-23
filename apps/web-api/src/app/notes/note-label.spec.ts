import { clientAbbreviation, cleanAbbreviation, composeNoteAudioLabel } from '@repo/shared';

describe('composeNoteAudioLabel', () => {
  const base = { index: '01', slotLabel: '_JAVERIANA_ESTEREO_AM_', date: '2026-11-19', abbreviation: 'ECOP', extension: 'mp3' };

  it('builds the name the team files notes under: number, program, date and client', () => {
    expect(composeNoteAudioLabel(base)).toBe('NOTA_01_JAVERIANA_ESTEREO_AM_19-11-26_ECOP.mp3');
  });

  it('does not repeat or leave dangling the underscores the program label comes with', () => {
    expect(composeNoteAudioLabel({ ...base, slotLabel: 'JAVERIANA_ESTEREO_AM' })).toBe('NOTA_01_JAVERIANA_ESTEREO_AM_19-11-26_ECOP.mp3');
    expect(composeNoteAudioLabel({ ...base, slotLabel: '__JAVERIANA_FIN_DE_SEMANA__' })).toBe('NOTA_01_JAVERIANA_FIN_DE_SEMANA_19-11-26_ECOP.mp3');
  });

  it('leaves out the date or the client when it is not known instead of leaving a gap', () => {
    expect(composeNoteAudioLabel({ ...base, date: undefined })).toBe('NOTA_01_JAVERIANA_ESTEREO_AM_ECOP.mp3');
    expect(composeNoteAudioLabel({ ...base, date: 'not a date' })).toBe('NOTA_01_JAVERIANA_ESTEREO_AM_ECOP.mp3');
    expect(composeNoteAudioLabel({ ...base, abbreviation: '' })).toBe('NOTA_01_JAVERIANA_ESTEREO_AM_19-11-26.mp3');
  });

  it('shows ## while the note has no number yet', () => {
    expect(composeNoteAudioLabel({ ...base, index: undefined })).toBe('NOTA_##_JAVERIANA_ESTEREO_AM_19-11-26_ECOP.mp3');
    expect(composeNoteAudioLabel({ ...base, index: '  ' })).toBe('NOTA_##_JAVERIANA_ESTEREO_AM_19-11-26_ECOP.mp3');
  });

  it('keeps the extension of the clip (a TV note is an mp4)', () => {
    expect(composeNoteAudioLabel({ ...base, extension: 'mp4' })).toMatch(/_ECOP\.mp4$/);
  });

  it('has no name until a program slot is chosen', () => {
    expect(composeNoteAudioLabel({ ...base, slotLabel: undefined })).toBeUndefined();
    expect(composeNoteAudioLabel({ ...base, slotLabel: '___' })).toBeUndefined();
  });
});

describe('clientAbbreviation', () => {
  it('uses the abbreviation the client has, cleaned for a file name', () => {
    expect(clientAbbreviation({ name: 'Prosperidad Social', abbreviation: ' dps ' })).toBe('DPS');
  });

  it('falls back to the first four letters of the name, upper case and without accents', () => {
    expect(clientAbbreviation({ name: 'Ecopetrol' })).toBe('ECOP');
    expect(clientAbbreviation({ name: 'Prosperidad Social', abbreviation: '' })).toBe('PROS');
    expect(clientAbbreviation({ name: 'Álvaro Núñez' })).toBe('ALVA');
  });

  it('keeps short names whole and gives nothing for a name with no letters', () => {
    expect(clientAbbreviation({ name: 'ISA' })).toBe('ISA');
    expect(clientAbbreviation({ name: '  ' })).toBe('');
    expect(clientAbbreviation({})).toBe('');
  });
});

describe('cleanAbbreviation', () => {
  it('keeps letters and digits only, in upper case, at most ten', () => {
    expect(cleanAbbreviation(' d.p.s ')).toBe('DPS');
    expect(cleanAbbreviation('Ñandú-9')).toBe('NANDU9');
    expect(cleanAbbreviation('ABCDEFGHIJKLMNO')).toBe('ABCDEFGHIJ');
    expect(cleanAbbreviation(undefined)).toBe('');
  });
});
