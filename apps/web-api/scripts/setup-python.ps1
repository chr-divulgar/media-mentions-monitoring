# Creates apps/web-api/.venv and installs requirements.txt in it, so the transcription script runs
# with its own Python packages whatever Pythons the machine has (resolvePython() uses this venv).
#
#   pnpm --filter web-api setup:python
#   pnpm --filter web-api setup:python -- -Python "C:\path\to\python.exe"
param(
    [string]$Python
)

$ErrorActionPreference = 'Stop'
$apiRoot = Split-Path -Parent $PSScriptRoot
$venv = Join-Path $apiRoot '.venv'

function Get-PythonVersion([string]$exe, [string[]]$prefix) {
    try { return (& $exe @prefix -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null) } catch { return $null }
}

# Pick the interpreter: the one asked for, else Python 3.11 through the py launcher, else `python`.
if (-not $Python) {
    $candidates = @(
        @{ Exe = 'py'; Prefix = @('-3.11') },
        @{ Exe = 'py'; Prefix = @('-3.12') },
        @{ Exe = 'python'; Prefix = @() }
    )
    foreach ($candidate in $candidates) {
        $version = Get-PythonVersion $candidate.Exe $candidate.Prefix
        if ($version) { $exe = $candidate.Exe; $prefix = $candidate.Prefix; break }
    }
} else {
    $exe = $Python
    $prefix = @()
    $version = Get-PythonVersion $exe $prefix
}

if (-not $version) { throw 'No Python found. Install Python 3.11 or pass -Python <path to python.exe>.' }
if ([version]"$version.0" -ge [version]'3.13.0') {
    Write-Warning "Python $version is very new: some audio libraries may be missing. Python 3.11 or 3.12 is recommended (pass -Python)."
}

Write-Host "Creating $venv with Python $version ..."
& $exe @prefix -m venv $venv
$venvPython = Join-Path $venv 'Scripts\python.exe'
& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install -r (Join-Path $apiRoot 'requirements.txt')

# Prove it works the way the API will run it.
& $venvPython -c "import speech_recognition as sr; print('speech_recognition', sr.__version__, 'OK')"
Write-Host "Done. The web-api will use $venvPython (set PYTHON_BIN to override)."
