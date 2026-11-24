# Abre a UI ActiveX de cada DTM do catálogo, cada um em processo isolado, e grava um resumo.
# Uso: powershell -ExecutionPolicy Bypass -File tools\ui-matrix.ps1
param([string]$Order = 'embed')   # ordem ActiveX: embed (padrão, §6.13.3) | init | site
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\LasecFDT.TestHost\bin\Debug\net48\LasecFDT.TestHost.exe'
$out = Join-Path $root 'docs\generated\ui\matrix'
New-Item -ItemType Directory -Force $out | Out-Null
$catalog = Get-Content (Join-Path $root 'docs\generated\dtm-catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @()
foreach ($d in $catalog.dtms) {
    $name = if ($d.progId) { $d.progId } else { $d.clsid }
    $report = Join-Path $out "$name.json"
    Remove-Item $report -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList @('--auto', $d.clsid, '--order', $Order, '--hold', '2', '--screenshot', "`"$out\$name.png`"", '--report', "`"$report`"") -PassThru -WindowStyle Normal
    if (-not $p.WaitForExit(60000)) { $p.Kill(); $code = 'timeout' } else { $code = $p.ExitCode }
    $row = [ordered]@{ dtm = $name; exitCode = $code; uiOpened = $null; colors = $null; closedCleanly = $null; note = '' }
    if (Test-Path $report) {
        $r = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
        $row.uiOpened = $r.uiOpened; $row.colors = $r.distinctColorsInControl; $row.closedCleanly = $r.closedCleanly
        $msg = $r.log | Where-Object { $_ -match 'Fun.+o .+:' -and $_ -notmatch '\[(trace|evento)\]' } | Select-Object -First 1
        if ($msg) { $row.note = ($msg.Substring(13)) }
    } elseif ($code -eq -1073741819) { $row.note = 'crash 0xC0000005 (violação de acesso no DTM)' }
    $rows += [pscustomobject]$row
    "{0,-10} {1,-40} ui={2} cores={3} fechou={4} {5}" -f $code, $name, $row.uiOpened, $row.colors, $row.closedCleanly, $row.note
}
$rows | ConvertTo-Json | Set-Content (Join-Path $out '_summary.json') -Encoding UTF8
