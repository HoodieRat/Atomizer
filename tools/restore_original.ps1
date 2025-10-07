param(
  [string]$Target = "input/t.js"
)

Write-Host "[restore_original] Target: $Target"
$targetPath = Resolve-Path $Target -ErrorAction SilentlyContinue
if (-not $targetPath) { $targetPath = Join-Path (Get-Location) $Target }
$targetDir = Split-Path $targetPath -Parent
$base = [System.IO.Path]::GetFileNameWithoutExtension($targetPath)
$ext  = [System.IO.Path]::GetExtension($targetPath)

# Candidate backups (prefer non-shim + largest size), search input dir then out dir
$candidates = @()

# 1) input/t_old.js (canonical)
$canon = Join-Path $targetDir ("{0}_old{1}" -f $base,$ext)
if (Test-Path $canon) { $candidates += $canon }

# 2) input/t_old_*.js (timestamped)
$stamp = Get-ChildItem -Path $targetDir -Filter ("{0}_old_*.js" -f $base) -ErrorAction SilentlyContinue | Sort-Object Length -Descending
if ($stamp) { $candidates += ($stamp | ForEach-Object { $_.FullName }) }

# 3) out/t_old.js (convenience copy)
$outDir = Join-Path (Get-Location) "out"
$outOld = Join-Path $outDir ("{0}_old{1}" -f $base,$ext)
if (Test-Path $outOld) { $candidates += $outOld }

function IsShim([string]$p) {
  try {
    $txt = Get-Content -Path $p -Raw -ErrorAction Stop
    return ($txt -match 'Shim re-exporting atomized bridge' -or ($txt -match 'export \* from' -and $txt -match '\.atomized\.js'))
  } catch { return $false }
}

# Pick the best candidate: first non-shim with length > 400, else largest
$best = $null
foreach ($c in $candidates | Get-Unique) {
  try {
    $len = (Get-Item $c).Length
    if (-not (IsShim $c) -and $len -gt 400) { $best = $c; break }
  } catch {}
}
if (-not $best -and $candidates.Count -gt 0) {
  $best = ($candidates | Sort-Object { (Get-Item $_).Length } -Descending | Select-Object -First 1)
}

if (-not $best) {
  Write-Host "[restore_original] No suitable backup found. Looked in: $targetDir and $outDir" -ForegroundColor Yellow
  exit 1
}

Copy-Item -Path $best -Destination $targetPath -Force
Write-Host "[restore_original] Restored $($best | Split-Path -Leaf) -> $Target" -ForegroundColor Green
