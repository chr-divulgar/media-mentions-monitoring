import * as path from 'path';
import { resolvePython } from './python-bin';
import { parseTranscriberResult } from './google-speech';

describe('resolvePython', () => {
  const cwd = path.resolve('/app/web-api');

  it('uses PYTHON_BIN when it is set', () => {
    expect(
      resolvePython({ env: { PYTHON_BIN: ' C:\\py\\python.exe ' }, cwd, exists: () => true }),
    ).toBe('C:\\py\\python.exe');
  });

  it("uses the project's virtual environment when it exists", () => {
    expect(resolvePython({ env: {}, cwd, platform: 'win32', exists: () => true })).toBe(
      path.resolve(cwd, '.venv', 'Scripts', 'python.exe'),
    );
    expect(resolvePython({ env: {}, cwd, platform: 'linux', exists: () => true })).toBe(
      path.resolve(cwd, '.venv', 'bin', 'python'),
    );
  });

  it('falls back to python from the PATH when there is no venv', () => {
    expect(resolvePython({ env: {}, cwd, exists: () => false })).toBe('python');
  });
});

describe('parseTranscriberResult', () => {
  it('reads the transcription of a successful run', () => {
    expect(parseTranscriberResult(0, 'output:hola mundo\n', '')).toEqual({ ok: true, text: 'hola mundo' });
  });

  it('keeps music/silence as a result, not a failure', () => {
    expect(parseTranscriberResult(0, 'output:Música-NoEspañol.\n', '')).toEqual({
      ok: true,
      text: 'Música-NoEspañol.',
    });
  });

  it('does not fail on a warning printed to stderr by a run that succeeded', () => {
    const result = parseTranscriberResult(0, 'output:hola\n', 'DeprecationWarning: something old');
    expect(result).toEqual({ ok: true, text: 'hola' });
  });

  it('explains a missing package and how to install it', () => {
    const result = parseTranscriberResult(
      1,
      '',
      "Traceback...\nModuleNotFoundError: No module named 'speech_recognition'\n",
    );
    expect(result.ok).toBe(false);
    expect(result.message).toContain('setup:python');
  });

  it('explains that Python itself was not found', () => {
    const result = parseTranscriberResult('ENOENT', '', '');
    expect(result.ok).toBe(false);
    expect(result.message).toContain('No se encontró Python');
  });

  it("reports the script's own error from stderr", () => {
    const result = parseTranscriberResult(1, '', 'Error: recognition service request failed (503)\n');
    expect(result).toEqual({ ok: false, message: 'Error: recognition service request failed (503)' });
  });

  it('still says something when stderr is empty', () => {
    const result = parseTranscriberResult(3, '', '');
    expect(result.ok).toBe(false);
    expect(result.message).toContain('código 3');
  });
});
