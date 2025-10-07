$srcPath = 'c:\Atomizer\input\t.js'
$ndjsonPath = 'c:\Atomizer\facts\facts.d\functions.ndjson'
$src = Get-Content -Raw -LiteralPath $srcPath
$len = $src.Length
$lines = Get-Content -LiteralPath $ndjsonPath
$problems = @()
$total = 0
foreach ($l in $lines) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    $obj = $null
    try { $obj = $l | ConvertFrom-Json } catch { continue }
    if ($null -eq $obj) { continue }
    $total++
    $start = [int]$obj.span.start
    $end = [int]$obj.span.end
    if ($start -lt 0 -or $end -lt 0 -or $start -ge $len -or $end -gt $len -or $end -le $start) {
        $problems += @{id=$obj.id;name=$obj.name;start=$start;end=$end;len=($end-$start);issue='oob'}
        continue
    }
    $slice = $src.Substring($start, $end - $start)

    $depth = 0
    $i = 0
    $strChar = $null
    $inLineComment = $false
    $inBlockComment = $false
    $balanced = $true
    while ($i -lt $slice.Length) {
        $c = $slice[$i]
        if ($inLineComment) {
            if ($c -eq "`n" -or $c -eq "`r") { $inLineComment = $false }
            $i++ ; continue
        }
        if ($inBlockComment) {
            if ($c -eq '*' -and $i+1 -lt $slice.Length -and $slice[$i+1] -eq '/') { $inBlockComment = $false; $i += 2; continue }
            $i++ ; continue
        }
        if ($strChar) {
            if ($c -eq '\\') { $i += 2; continue }
            if ($c -eq $strChar) { $strChar = $null; $i++; continue }
            if ($strChar -eq '`' -and $c -eq '$' -and $i+1 -lt $slice.Length -and $slice[$i+1] -eq '{') { $depth++; $i += 2; continue }
            if ($strChar -eq '`' -and $c -eq '}' -and $depth -gt 0) { $depth--; $i++; continue }
            $i++; continue
        }
        if ($c -eq '/' -and $i+1 -lt $slice.Length) {
            $n = $slice[$i+1]
            if ($n -eq '/') { $inLineComment = $true; $i += 2; continue }
            if ($n -eq '*') { $inBlockComment = $true; $i += 2; continue }
        }
        if ($c -eq '"' -or $c -eq "'" -or $c -eq '`') { $strChar = $c; $i++; continue }
        if ($c -eq '{') { $depth++; $i++; continue }
        if ($c -eq '}') { $depth--; if ($depth -lt 0) { $balanced = $false; break } $i++; continue }
        $i++
    }
    if ($strChar -ne $null -or $inLineComment -or $inBlockComment -or $depth -ne 0 -or -not $balanced) {
        $problems += @{id=$obj.id;name=$obj.name;start=$start;end=$end;len=($end-$start);depth=$depth;strChar=$strChar;issue='unbalanced'}
    } else {
        if (($end - $start) -lt 80) { $problems += @{id=$obj.id;name=$obj.name;start=$start;end=$end;len=($end-$start);issue='small'} }
    }
}

$reportPath = 'c:\Atomizer\tools\span_report.txt'
$out = @()
$out += ("Total functions: {0}; Problems: {1}" -f $total, $problems.Count)
foreach ($p in $problems) {
    $out += ("{0} {1} {2}->{3} len={4} issue={5} depth={6} str={7}" -f $p.id, $p.name, $p.start, $p.end, $p.len, $p.issue, ($p.depth -as [string]), ($p.strChar -as [string]))
}
$out | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output ($out -join "`n")
