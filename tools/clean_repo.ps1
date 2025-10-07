param(
    [switch]$DryRun
)

# Clean generated artifacts and local build outputs. Safe to run anytime.
$ErrorActionPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
Write-Host "Repo root: $root"

$folders = @(
    'out',
    'out_cjs',
    'output',
    'output_debug',
    'facts',
    'plans',
    'bin',
    'obj',
    'tests/bin',
    'tests/obj',
    '.vs'
)

$patterns = @(
    '*_old.js',
    '*_old_*.js',
    '*.js.atomized.js',
    '*.cjs'
)

# Remove folders
foreach ($rel in $folders) {
    $path = Join-Path $root $rel
    if (Test-Path $path) {
        if ($DryRun) { Write-Host "[DryRun] Would remove folder: $rel" }
        else { Write-Host "Removing folder: $rel"; Remove-Item -Recurse -Force $path }
    }
}

# Remove files matching patterns (recursive)
foreach ($pat in $patterns) {
    $files = Get-ChildItem -Path $root -Filter $pat -Recurse -File -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $rel = $f.FullName.Replace($root + [IO.Path]::DirectorySeparatorChar, '')
        if ($DryRun) { Write-Host "[DryRun] Would remove: $rel" }
        else { Write-Host "Removing: $rel"; Remove-Item -Force $f.FullName }
    }
}

Write-Host "Clean complete."