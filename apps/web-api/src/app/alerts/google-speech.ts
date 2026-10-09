import speech from '@google-cloud/speech';
import * as fs from 'fs';
import { protos } from '@google-cloud/speech';
import { execFile } from 'child_process';
import { resolvePython } from './python-bin';

export async function transcribeWav(
  filePath: string,
  languageCode = 'es-ES',
): Promise<string> {
  const client = new speech.SpeechClient();
  const file = fs.readFileSync(filePath);
  const audioBytes = file.toString('base64');
  const audio = { content: audioBytes };
  const config = {
    encoding:
      protos.google.cloud.speech.v1.RecognitionConfig.AudioEncoding.LINEAR16,
    sampleRateHertz: 16000,
    languageCode,
  };
  const request = { audio, config };
  const [response] = await client.recognize(request);
  const transcription =
    response.results?.map((r) => r.alternatives?.[0]?.transcript).join(' ') ||
    '';
  return transcription;
}

const SETUP_HINT =
  'Falta instalar las dependencias de Python de la transcripción: ejecuta "pnpm --filter web-api setup:python" (o define PYTHON_BIN con un Python que tenga SpeechRecognition).';

// `text` when the script succeeded, `message` (already readable) when it did not.
export interface TranscriberResult {
  ok: boolean;
  text?: string;
  message?: string;
}

/**
 * Reads what `getText.py` did. Exit code 0 is a transcription (a warning on stderr does not make
 * it fail); anything else is an error, explained from stderr: the script reports its own failures
 * (network, unreadable file) there, and Python reports a missing package or a crash there too.
 */
export function parseTranscriberResult(
  code: number | string | null,
  stdout: string,
  stderr: string,
): TranscriberResult {
  if (code === 0) {
    const match = /output:(.*)/.exec(stdout);
    return { ok: true, text: (match ? match[1] : stdout).trim() };
  }

  if (code === 'ENOENT') {
    return {
      ok: false,
      message: `No se encontró Python. ${SETUP_HINT}`,
    };
  }
  if (/No module named ['"]?speech_recognition/.test(stderr)) {
    return { ok: false, message: SETUP_HINT };
  }

  const detail = stderr
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(-3)
    .join(' | ');
  return {
    ok: false,
    message: detail || `El script de transcripción terminó con código ${code}.`,
  };
}

export function transcribeWithPython(chunkPath: string): Promise<string> {
  return new Promise((resolve, reject) => {
    execFile(
      resolvePython(),
      ['./scripts/getText.py', chunkPath],
      { encoding: 'utf8' },
      (error, stdout, stderr) => {
        const code = error
          ? ((error as NodeJS.ErrnoException).code ?? 1)
          : 0;
        const result = parseTranscriberResult(
          typeof code === 'number' || code === 'ENOENT' ? code : 1,
          stdout,
          stderr,
        );
        if (result.ok) resolve(result.text ?? '');
        else reject(new Error(result.message));
      },
    );
  });
}
