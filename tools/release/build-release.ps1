<#
  Gera o instalador do LasecFDT (MSI de 32 bits, como o motor e os DTMs) e um zip portátil.
    tools\release\build-release.ps1 -Version 0.2.0 [-WixDir C:\wix\bin] [-SkipFlutter] [-SkipDotnet]
  Saída: artifacts\LasecFDT-<versão>.msi e artifacts\LasecFDT-<versão>-portable.zip
  Usado localmente e pelo workflow .github/workflows/release.yml (mesmos passos).
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$WixDir,
  [switch]$SkipFlutter,
  [switch]$SkipDotnet,
  # MSI SÓ PARA USO INTERNO: inclui os schemas HART da FDT 1.2 (specs\FDT-1.2\XML-Files, licença PNO "solely for
  # internal use by the User", sem repasse a terceiros). Nunca publicar: o script recusa rodar no GitHub Actions.
  [switch]$InternalHart
)
$ErrorActionPreference = 'Stop'
if ($InternalHart -and $env:GITHUB_ACTIONS) { throw 'O MSI de uso interno (schemas HART da FDT 1.2) não pode ser gerado no GitHub Actions.' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$ver = $Version.TrimStart('v')
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida '$Version' (use X.Y.Z, ex.: 0.2.0)." }
$art = Join-Path $root 'artifacts'
$stage = Join-Path $art 'stage\LasecFDT'
$engine = Join-Path $stage 'engine'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Exec([string]$exe, [string[]]$a) {
  & $exe @a
  if ($LASTEXITCODE -ne 0) { throw "$exe terminou com código $LASTEXITCODE" }
}

# ---------------------------------------------------------------- WiX 3
if (-not $WixDir) {
  $cands = @($env:WIX, "${env:ProgramFiles(x86)}\WiX Toolset v3.14", "${env:ProgramFiles(x86)}\WiX Toolset v3.11") | Where-Object { $_ }
  foreach ($c in $cands) { foreach ($d in @($c, (Join-Path $c 'bin'))) { if (Test-Path (Join-Path $d 'candle.exe')) { $WixDir = $d; break } } if ($WixDir) { break } }
  if (-not $WixDir) { $cmd = Get-Command candle.exe -ErrorAction SilentlyContinue; if ($cmd) { $WixDir = Split-Path $cmd.Source } }
}
if (-not $WixDir -or -not (Test-Path (Join-Path $WixDir 'candle.exe'))) { throw 'WiX 3 (candle/light/heat) não encontrado: informe -WixDir.' }

# ---------------------------------------------------------------- motor (.NET Framework 4.8 x86)
if (-not $SkipDotnet) {
  Step "Compilando o motor $ver (Release)"
  foreach ($p in 'src\LasecFDT.Host\LasecFDT.Host.csproj', 'src\LasecFDT.Worker.Fdt1\LasecFDT.Worker.Fdt1.csproj', 'src\LasecFDT.Cli\LasecFDT.Cli.csproj') {
    Exec dotnet @('build', (Join-Path $root $p), '-c', 'Release', '-nologo', '-v', 'q', "-p:Version=$ver", "-p:FileVersion=$ver.0", "-p:AssemblyVersion=$ver.0")
  }
}

# ---------------------------------------------------------------- UI (Flutter Windows x64)
$ui = Join-Path $root 'ui\lasecfdt_app'
if (-not $SkipFlutter) {
  Step 'Compilando a UI (Flutter, release)'
  Push-Location $ui
  try {
    $build = [int]((Get-Date).ToUniversalTime() - [datetime]'2026-01-01').TotalHours
    Exec flutter @('build', 'windows', '--release', "--build-name=$ver", "--build-number=$build")
  } finally { Pop-Location }
}
$uiOut = Join-Path $ui 'build\windows\x64\runner\Release'
if (-not (Test-Path (Join-Path $uiOut 'lasecfdt_ui.exe'))) { throw "UI não compilada: $uiOut" }

# ---------------------------------------------------------------- montagem
Step 'Montando a pasta de instalação'
if (Test-Path (Join-Path $art 'stage')) { Remove-Item (Join-Path $art 'stage') -Recurse -Force }
New-Item -ItemType Directory -Force $engine | Out-Null
Copy-Item (Join-Path $uiOut '*') $stage -Recurse

# Runtime VC++ ao lado da UI (implantação local permitida pela Microsoft; a UI Flutter depende dele).
$crt = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
  foreach ($vs in (& $vswhere -products * -property installationPath)) {
    $c = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
         ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue } | Select-Object -First 1
    if ($c) { $crt = $c.FullName; break }
  }
}
$crtFiles = 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'
foreach ($f in $crtFiles) {
  $src = if ($crt -and (Test-Path (Join-Path $crt $f))) { Join-Path $crt $f } else { Join-Path $env:WINDIR "System32\$f" }
  if (-not (Test-Path $src)) { throw "Runtime VC++ não encontrado: $f" }
  Copy-Item $src $stage
}
Write-Host "   runtime VC++: $(if ($crt) { $crt } else { 'System32' })"

# Motor: Host + worker + CLI na mesma pasta (o Host acha o worker ao lado). Sem schemas de desenvolvimento
# (FDTSchemas\ vem da especificação local, que não é redistribuível); sem símbolos.
foreach ($p in 'LasecFDT.Host', 'LasecFDT.Worker.Fdt1', 'LasecFDT.Cli') {
  $out = Join-Path $root "src\$p\bin\Release\net48"
  if (-not (Test-Path $out)) { throw "Saída não encontrada: $out" }
  Get-ChildItem $out -File | Where-Object { $_.Extension -notin '.pdb', '.xml' } | Copy-Item -Destination $engine -Force
}
if (Test-Path (Join-Path $engine 'FDTSchemas')) { Remove-Item (Join-Path $engine 'FDTSchemas') -Recurse -Force }
foreach ($must in 'LasecFDT.Host.exe', 'LasecFDT.Worker.Fdt1.exe', 'lasecfdt.exe', 'Jigfdt.fdt100.dll', 'FDT100.dll') {
  if (-not (Test-Path (Join-Path $engine $must))) { throw "Faltou no motor: $must" }
}

if ($InternalHart) {
  # Schemas HART na RAIZ de engine\FDT XML Schemas\ (ao lado dos do merge module 1.2.1; é na raiz que os DTMs procuram).
  # Arquivos copiados sem alteração (a licença proíbe mudar ou remover avisos de copyright).
  Step 'Uso interno: incluindo os schemas HART da FDT 1.2'
  $hartSrc = Join-Path $root 'specs\FDT-1.2\XML-Files'
  $hartDst = Join-Path $engine 'FDT XML Schemas'
  New-Item -ItemType Directory -Force $hartDst | Out-Null
  foreach ($f in 'DTMHARTDeviceSchema.xml', 'FDTHARTChannelParameterSchema.xml', 'FDTHARTCommunicationSchema.xml') {
    $src = Join-Path $hartSrc $f
    if (-not (Test-Path $src)) { throw "Schema HART não encontrado: $src (pasta specs\ da especificação licenciada)" }
    Copy-Item $src $hartDst
  }
  Set-Content -Encoding utf8 (Join-Path $engine 'LEIA-ME-USO-INTERNO.txt') @"
Esta cópia do LasecFDT contém schemas XML da especificação FDT, inclusive os HART da FDT 1.2
(MSI: engine\FDT XML Schemas\*HART*.xml; portátil: engine\FDTSchemas\),
Copyright Profibus Nutzerorganisation e.V. (PNO).

A licença da PNO concede esses arquivos "solely for internal use by the User" e proíbe
alugá-los ou emprestá-los a terceiros. Este pacote é de USO INTERNO do licenciado:
não o publique, não o envie e não o instale em computadores de terceiros.
"@
}

# ---------------------------------------------------------------- MSI
Step 'Gerando o MSI (WiX)'
$obj = Join-Path $art 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$heat = Join-Path $WixDir 'heat.exe'; $candle = Join-Path $WixDir 'candle.exe'; $light = Join-Path $WixDir 'light.exe'
Exec $heat @('dir', $stage, '-nologo', '-cg', 'AppFiles', '-dr', 'INSTALLFOLDER', '-srd', '-sreg', '-scom', '-ag', '-var', 'var.StageDir', '-out', (Join-Path $obj 'AppFiles.wxs'))
$defs = @("-dStageDir=$stage", "-dVersion=$ver", "-dInternal=$(if ($InternalHart) { 1 } else { 0 })", "-dRedistDir=$(Join-Path $root 'lib\fdt-jig\redist')", "-dIconFile=$(Join-Path $ui 'windows\runner\resources\app_icon.ico')", "-dReadmeSchemas=$(Join-Path $root 'installer\LEIA-ME-schemas.txt')")
# Barra final dupla: num caminho com espaço, "...\obj\" escaparia a aspa ao montar a linha de comando.
Exec $candle (@('-nologo', '-arch', 'x86', '-ext', 'WixUtilExtension', '-ext', 'WixUIExtension', '-out', ($obj + '\\')) + $defs + @((Join-Path $root 'installer\LasecFDT.wxs'), (Join-Path $obj 'AppFiles.wxs')))
$msi = Join-Path $art "LasecFDT-$ver$(if ($InternalHart) { '-USO-INTERNO' }).msi"
# ICE: avisos conhecidos de merge modules de terceiros (FDT) e de arquivos sem versão não impedem a instalação.
Exec $light @('-nologo', '-ext', 'WixUtilExtension', '-ext', 'WixUIExtension', '-cultures:pt-BR', '-spdb', '-sw1055', '-sw1056', '-sw1076', '-sice:ICE99', '-out', $msi, (Join-Path $obj 'LasecFDT.wixobj'), (Join-Path $obj 'AppFiles.wixobj'))

Write-Host ''
Write-Host "MSI:      $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)" -ForegroundColor Green
if ($InternalHart) {
  # Portátil interno: sem MSI não há merge module de schemas, então leva a pasta completa engine\FDTSchemas\
  # (a mesma do desenvolvimento: FDT 1.2 com HART + 1.2.1 + anexo PROFIBUS; FdtSchemaLocator a usa primeiro).
  Step 'Gerando o zip portátil de uso interno'
  $pstage = Join-Path $art 'stage-interno\LasecFDT'
  if (Test-Path (Split-Path $pstage)) { Remove-Item (Split-Path $pstage) -Recurse -Force }
  New-Item -ItemType Directory -Force $pstage | Out-Null
  Copy-Item (Join-Path $stage '*') $pstage -Recurse
  Remove-Item (Join-Path $pstage 'engine\FDT XML Schemas') -Recurse -Force
  $devSchemas = Join-Path $root 'src\LasecFDT.Worker.Fdt1\bin\Release\net48\FDTSchemas'
  if (-not (Test-Path (Join-Path $devSchemas 'FDTHARTCommunicationSchema.xml'))) { throw "Schemas completos não encontrados: $devSchemas" }
  Copy-Item $devSchemas (Join-Path $pstage 'engine\FDTSchemas') -Recurse
  $zip = Join-Path $art "LasecFDT-$ver-USO-INTERNO-portable.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path (Join-Path $pstage '*') -DestinationPath $zip
  Remove-Item (Split-Path $pstage) -Recurse -Force
  Write-Host "Portátil: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
  Write-Host 'USO INTERNO: contém schemas da especificação FDT (licença PNO). Não publique nem repasse a terceiros.' -ForegroundColor Yellow
} else {
  Step 'Gerando o zip portátil'
  $zip = Join-Path $art "LasecFDT-$ver-portable.zip"
  if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
  Write-Host "Portátil: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
}
