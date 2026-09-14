#!/usr/bin/env python3
"""
Generar cookies de YouTube sin dependencia del navegador en runtime.
Uso: python scripts/setup_youtube_cookies.py
"""

import os
import sys
import json
from pathlib import Path

def setup_youtube_cookies():
    """Generate YouTube cookies file from browser.

    Writes to the canonical path shared with NestJS (shared-cookies/ at the repo
    root), the same one worker-options.json's youtubeCookiesFilePath points to.
    """

    cookies_dir = Path("../../shared-cookies")
    cookies_file = cookies_dir / "youtube-cookies.txt"
    
    # Crear directorio si no existe
    cookies_dir.mkdir(parents=True, exist_ok=True)
    print(f"[OK] Directorio {cookies_dir} verificado")
    
    # Intentar instalar/actualizar yt-dlp
    print("[INFO] Descargando/actualizando yt-dlp...")
    try:
        import yt_dlp
        print(f"[OK] yt-dlp {yt_dlp.__version__} disponible")
    except ImportError:
        print("[INFO] Instalando yt-dlp desde PyPI...")
        os.system(f"{sys.executable} -m pip install yt-dlp -q")
        try:
            import yt_dlp
            print(f"[OK] yt-dlp {yt_dlp.__version__} instalado")
        except ImportError:
            print("[ERROR] No se pudo instalar yt-dlp")
            return False
    
    # Generar cookies desde Edge
    print("[INFO] Extrayendo cookies de Edge...")
    print("       (Edge puede abrirse brevemente)")
    
    try:
        import yt_dlp
        
        # Opciones para extraer cookies
        ydl_opts = {
            'quiet': True,
            'no_warnings': True,
            'socket_timeout': 10,
        }
        
        # Crear instancia de yt-dlp
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            # Usar --cookies-from-browser internamente
            # Yt-dlp almacenará cookies automáticamente
            info = ydl.extract_info('https://www.youtube.com', download=False)
        
        # La otra opción es usar directamente el CLI de yt-dlp
        # para extraer cookies con --cookies-from-browser
        print("[INFO] Usando yt-dlp CLI para extraer cookies...")
        
        # Ejecutar yt-dlp directamente con subprocess
        import subprocess
        
        cmd = [
            sys.executable, '-m', 'yt_dlp',
            '--cookies-from-browser', 'edge',
            '--cookies', str(cookies_file),
            '--extract-audio',
            '--quiet',
            '--no-warnings',
            'https://www.youtube.com'
        ]
        
        result = subprocess.run(cmd, capture_output=True, text=True)
        
        if result.returncode != 0 and "ERROR" in result.stderr:
            print(f"[WARNING] yt-dlp stderr: {result.stderr[:200]}")
        
        # Verificar que se creó el archivo
        if cookies_file.exists():
            file_size = cookies_file.stat().st_size
            print(f"[OK] Cookies generadas exitosamente")
            print(f"     Archivo: {cookies_file}")
            print(f"     Tamano: {file_size} bytes")
            print()
            print("[CONFIG] Configuracion para worker-options.json:")
            print('         "useBrowserCookies": false')
            print('         "youtubeCookiesFilePath": "stage/cookies/youtube-cookies.txt"')
            print()
            print("[INFO] Las cookies son reutilizables.")
            print("       El worker NO necesita Edge en runtime.")
            return True
        else:
            print("[ERROR] No se genero el archivo de cookies")
            print("        Intenta manualmente:")
            print(f"        yt-dlp --cookies-from-browser edge --cookies {cookies_file} https://www.youtube.com")
            return False
            
    except Exception as e:
        print(f"[ERROR] Exception: {e}")
        return False

if __name__ == "__main__":
    success = setup_youtube_cookies()
    sys.exit(0 if success else 1)
