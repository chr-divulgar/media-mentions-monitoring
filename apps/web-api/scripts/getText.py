import speech_recognition as sr
from os import path
import sys
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')


out_label = "output:"


def get_text(pathname):
    AUDIO_FILE = path.join(path.dirname(path.realpath(__file__)), pathname)
    r = sr.Recognizer()
    with sr.AudioFile(AUDIO_FILE) as source:
        audio = r.record(source)
    try:
        return out_label + r.recognize_google(audio, language='es-CO')
    except sr.UnknownValueError:
        # Audio without recognizable Spanish speech (music, silence): a result, not a failure.
        return out_label + "Música-NoEspañol."


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python getText.py <audio_path>", file=sys.stderr)
        sys.exit(2)
    try:
        print(get_text(sys.argv[1]))
    except sr.RequestError as e:
        # The recognition service could not be reached or refused the request.
        print(f"Error: recognition service request failed ({e})", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"Error: file {sys.argv[1]} cannot be read as audio. Details: {e}", file=sys.stderr)
        sys.exit(1)
