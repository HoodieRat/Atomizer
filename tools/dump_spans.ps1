$srcPath = 'c:\Atomizer\input\t.js'
$ndjsonPath = 'c:\Atomizer\facts\facts.d\functions.ndjson'
$src = Get-Content -Raw -LiteralPath $srcPath
$len = $src.Length
$lines = Get-Content -LiteralPath $ndjsonPath
$count = 0
foreach ($l in $lines) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    if ($count -ge 12) { break }
    $obj = $null
    try { $obj = $l | ConvertFrom-Json } catch { continue }
    if ($null -eq $obj) { continue }
    $start = [int]$obj.span.start
    $end = [int]$obj.span.end
    $lenSlice = $end - $start
    $slice = ''
    if ($start -ge 0 -and $end -gt $start -and $end -le $src.Length) { $slice = $src.Substring($start, [math]::Min($lenSlice, 800)) }
    Write-Output "--- ${($obj.id)} ${($obj.name)} ${start}-${end} len=${lenSlice} ---"
    if ($slice.Length -gt 0) {
        $first = $slice.Substring(0, [math]::Min(200, $slice.Length)).Replace("`r`n","\n")
        $last = if ($slice.Length -gt 200) { $slice.Substring([math]::Max(0,$slice.Length-80)).Replace("`r`n","\n") } else { '' }
        Write-Output "FIRST: $first"
        if ($last) { Write-Output "LAST: $last" }
    } else { Write-Output "[slice out of bounds or empty]" }
    $count++
}
