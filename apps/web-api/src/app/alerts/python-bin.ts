import * as fs from 'fs';
import * as path from 'path';

// The scripts under apps/web-api/scripts (getText.py) need Python packages the project installs in
// its own virtual environment. Relying on whatever `python` the PATH offers first made the
// transcription fail on a machine with several Pythons (the first one had no packages).

interface PythonLookup {
  env?: NodeJS.ProcessEnv;
  cwd?: string;
  platform?: NodeJS.Platform;
  exists?: (file: string) => boolean;
}

/**
 * The Python to run the scripts with: PYTHON_BIN when set, else the project's own virtual
 * environment (`.venv`, created by `pnpm --filter web-api setup:python`), else `python` from the PATH.
 */
export function resolvePython({
  env = process.env,
  cwd = process.cwd(),
  platform = process.platform,
  exists = fs.existsSync,
}: PythonLookup = {}): string {
  const configured = env.PYTHON_BIN?.trim();
  if (configured) return configured;

  const venvPython =
    platform === 'win32'
      ? path.resolve(cwd, '.venv', 'Scripts', 'python.exe')
      : path.resolve(cwd, '.venv', 'bin', 'python');
  return exists(venvPython) ? venvPython : 'python';
}
