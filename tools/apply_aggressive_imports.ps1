# Aggressive import applier for AtomizeJs
# Scans out/*.js for token matches of function names (from facts + plan) and inserts import lines.
# Writes plans/imports.applied.json with the applied imports map.

param()

$cwd = Get-Location
$plansDir = Join-Path $cwd "plans"
$outDir = Join-Path $cwd "out"
$factsPath = Join-Path $cwd "facts\facts.d\functions.ndjson"
$planPath = Join-Path $plansDir "plan.json"

if (-not (Test-Path $planPath)) { Write-Host "plan.json not found. Run pipeline first."; exit 1 }
if (-not (Test-Path $factsPath)) { Write-Host "functions.ndjson not found. Run Analyze first."; exit 1 }
if (-not (Test-Path $outDir)) { Write-Host "out directory not found. Run Write first."; exit 1 }

# build id->name map
$idToName = @{}
Get-Content $factsPath | ForEach-Object {
    try {
        $obj = $_ | ConvertFrom-Json -ErrorAction Stop
        if ($obj.id -and $obj.name) { $idToName[$obj.id] = $obj.name }
    } catch { }
}

# build id->module map from plan.json
$idToModule = @{}
try {
    $plan = Get-Content $planPath -Raw | ConvertFrom-Json
    foreach ($mod in $plan.modules) {
        $slug = $mod.slug
        foreach ($fid in $mod.functions) { $idToModule[$fid] = $slug }
    }
} catch {
    Write-Host "Failed to parse plan.json: $_"; exit 1
}

# build name->module map
$nameToModule = @{}
foreach ($kv in $idToName.GetEnumerator()) {
    $id = $kv.Key; $name = $kv.Value
    if ($idToModule.ContainsKey($id)) { $nameToModule[$name] = $idToModule[$id] }
}

if ($nameToModule.Count -eq 0) { Write-Host "No function name -> module mapping available; aborting."; exit 0 }

# For each out file, scan for token matches and build imports
$applied = @{}
$jsFiles = Get-ChildItem -Path $outDir -Filter "*.js" -File | Sort-Object Name
foreach ($file in $jsFiles) {
    $slug = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $text = Get-Content $file.FullName -Raw
    $imports = New-Object System.Collections.Generic.HashSet[string]

    foreach ($kv in $nameToModule.GetEnumerator()) {
        $fn = $kv.Key; $mod = $kv.Value
        if ($mod -eq $slug) { continue }
        # token regex; require word boundary and not preceded by '.' (property access)
        $pattern = "(?<![\.0-9A-Za-z_\$])\b" + [System.Text.RegularExpressions.Regex]::Escape($fn) + "\b"
        if ([System.Text.RegularExpressions.Regex]::IsMatch($text, $pattern)) {
            # avoid adding if already imported
            if ($text -match "import\s*\{\s*" + [System.Text.RegularExpressions.Regex]::Escape($fn) + "\s*\}") { continue }
            # avoid if the match appears inside an export line in this file
            if ($text -match "export\s*\{[^{]*\b" + [System.Text.RegularExpressions.Regex]::Escape($fn) + "\b") { continue }
            $relPath = "./$mod.js"
            $imports.Add("import { $fn } from '$relPath';") | Out-Null
        }
    }

    if ($imports.Count -gt 0) {
        # prepare lines and insert after top banner / existing import block
        $lines = Get-Content $file.FullName
        $insertAt = 0
        # skip initial shebang or 'use strict' or single-line comments or existing imports
        while ($insertAt -lt $lines.Length) {
            $t = $lines[$insertAt].Trim()
            if ($t -eq "" -or $t -like "'use strict'*" -or $t -like '"use strict"*' -or $t.StartsWith("//") -or $t.StartsWith("import ")) { $insertAt++; continue }
            # if a block comment starts, skip until the closing */ line
            if ($t.StartsWith("/*")) {
                while ($insertAt -lt $lines.Length -and -not ($lines[$insertAt].Contains("*/"))) { $insertAt++ }
                if ($insertAt -lt $lines.Length) { $insertAt++ } # skip the line containing */
                continue
            }
            break
        }

        # Clean any accidental import-like lines in the header region to avoid commented imports remaining
        $headerEnd = [Math]::Min($insertAt - 1, $lines.Length - 1)
        $header = @()
        if ($headerEnd -ge 0) {
            for ($i = 0; $i -le $headerEnd; $i++) {
                $ln = $lines[$i]
                if ($ln -match "^\s*(import\s*\{)" ) { continue } # drop import-like lines in header
                $header += $ln
            }
        }

        $newLines = @()
        if ($header.Count -gt 0) { $newLines += $header }
        $newLines += $imports
        if ($insertAt -lt $lines.Length) { $newLines += $lines[$insertAt..($lines.Length-1)] }

        # write backup and apply
        Copy-Item -Path $file.FullName -Destination ($file.FullName + ".bak") -Force
        $newLines | Set-Content -Path $file.FullName -Encoding UTF8
        $applied[$slug] = $imports
        Write-Host "Applied imports to $($file.Name): $($imports.Count) imports"
    }
}

# If nothing was applied, attempt a cleanup pass: move import-like lines out of top block comments into active imports
if ($applied.Count -eq 0) {
    $cleanupApplied = @{}
    foreach ($file in $jsFiles) {
        $slug = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $lines = Get-Content $file.FullName
        if ($lines.Length -lt 3) { continue }
        # detect a leading block comment starting somewhere in the top region (allow banners before it)
        $blockStart = -1
        $scanLimit = [Math]::Min(40, $lines.Length - 1)
        for ($si = 0; $si -le $scanLimit; $si++) {
            if ($lines[$si].TrimStart().StartsWith("/*")) { $blockStart = $si; break }
        }
        if ($blockStart -lt 0) { continue }
        # find end of block
        $end = $blockStart
            while ($end -lt $lines.Length -and -not ($lines[$end].Contains("*/"))) { $end++ }
            if ($end -ge $lines.Length) { continue }
            # scan block for import-like lines
            $foundImports = @()
            for ($i = $blockStart; $i -le $end; $i++) {
                $ln = $lines[$i]
                if ($ln -match "import\s*\{\s*[A-Za-z_\$][\w\$]*\s*\}") { $foundImports += $ln }
            }
            if ($foundImports.Count -gt 0) {
                # remove them from the block
                $newBlock = @()
                for ($i = $blockStart; $i -le $end; $i++) {
                    $ln = $lines[$i]
                    if ($ln -match "import\s*\{\s*[A-Za-z_\$][\w\$]*\s*\}") { continue }
                    $newBlock += $ln
                }
                # rebuild file: lines before block, new block, then imports, then rest
                $before = if ($blockStart -gt 0) { $lines[0..($blockStart-1)] } else { @() }
                $after = if ($end -lt $lines.Length -1) { $lines[($end+1)..($lines.Length-1)] } else { @() }
                $newLines = @()
                $newLines += $before
                $newLines += $newBlock
                $newLines += $foundImports
                $newLines += $after

                # backup and write
                Copy-Item -Path $file.FullName -Destination ($file.FullName + ".bak2") -Force
                $newLines | Set-Content -Path $file.FullName -Encoding UTF8
                $cleanupApplied[$slug] = $foundImports
                Write-Host "Cleaned commented imports from $($file.Name): moved $($foundImports.Count) imports into active header"
            }
        }
    if ($cleanupApplied.Count -gt 0) {
        New-Item -ItemType Directory -Path $plansDir -Force | Out-Null
        $json = $cleanupApplied | ConvertTo-Json -Depth 5
        Set-Content -Path (Join-Path $plansDir "imports.applied.json") -Value $json -Encoding UTF8
        Write-Host "Wrote plans/imports.applied.json (cleanup pass)"
    } else {
        Write-Host "No aggressive imports applied."
    }
} else {
    # write applied summary
    $out = @{}
    foreach ($k in $applied.Keys) { $out[$k] = $applied[$k] }
    New-Item -ItemType Directory -Path $plansDir -Force | Out-Null
    $json = $out | ConvertTo-Json -Depth 5
    Set-Content -Path (Join-Path $plansDir "imports.applied.json") -Value $json -Encoding UTF8
    Write-Host "Wrote plans/imports.applied.json"
}

Write-Host "Done."