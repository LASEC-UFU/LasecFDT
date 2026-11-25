import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';

/// Versão publicada no GitHub mais nova que a instalada.
class UpdateRelease {
  const UpdateRelease({
    required this.tag,
    required this.version,
    required this.assetName,
    required this.downloadUri,
    required this.pageUri,
    this.size = 0,
    this.notes = '',
    this.sha256,
  });

  final String tag;
  final String version;
  final String assetName;
  final Uri downloadUri;
  final Uri pageUri;
  final int size;
  final String notes;
  final String? sha256;
}

/// Atualização pelo GitHub (o mesmo esquema do Gestor UFU): ao abrir, a UI consulta o último Release; se houver
/// versão mais nova, pergunta ao operador, baixa o MSI (com verificação SHA-256 quando o GitHub informa o digest),
/// fecha o LasecFDT e um auxiliar em PowerShell roda o Windows Installer (UAC) e reabre o programa.
///
/// Só vale para a instalação do MSI (Arquivos de Programas\LasecFDT). Cópia portátil e desenvolvimento não são
/// verificados. Sem internet ou com o GitHub fora do ar, nada acontece.
///
/// Teste: LASECFDT_UPDATE_TEST_VERSION=X.Y.Z faz esta UI (mesmo fora da instalação) agir como a instalação padrão
/// nessa versão. Só para validar o fluxo; não usar em produção.
class UpdateService {
  static final _latest = Uri.https('api.github.com', '/repos/LASEC-UFU/LasecFDT/releases/latest');
  static const _agent = 'LasecFDT-Windows-Updater';
  static const _exe = 'lasecfdt_ui.exe';

  final Map<String, String> _env = Platform.environment;

  String? get _testVersion {
    final v = _env['LASECFDT_UPDATE_TEST_VERSION']?.trim();
    return v == null || v.isEmpty ? null : v;
  }

  List<String> get _installRoots => [
        for (final k in ['ProgramFiles(x86)', 'ProgramFiles'])
          if ((_env[k] ?? '').trim().isNotEmpty) '${_env[k]!.trim()}\\LasecFDT',
      ];

  /// Pasta da instalação (MSI) em que esta UI roda; null fora dela (portátil, desenvolvimento).
  String? get installDir {
    if (!Platform.isWindows) return null;
    if (_testVersion != null) {
      for (final r in _installRoots) {
        if (File('$r\\$_exe').existsSync()) return r;
      }
      return null;
    }
    final exe = File(Platform.resolvedExecutable).absolute.path;
    final dir = exe.substring(0, exe.lastIndexOf('\\'));
    for (final r in _installRoots) {
      if (dir.toLowerCase() == r.toLowerCase()) return dir;
    }
    return null;
  }

  bool get enabled => installDir != null;

  /// Instalação de uso interno (com os schemas HART da FDT 1.2 dentro do engine). O MSI público não os traz, mas o
  /// próprio MSI os copia para a pasta do operador antes de remover a versão anterior (PreserveHartSchemas).
  bool get isInternal {
    final d = installDir;
    return d != null && File('$d\\engine\\LEIA-ME-USO-INTERNO.txt').existsSync();
  }

  /// Pasta de schemas do operador (a mesma que FdtSchemaLocator.PlantSchemaFolder lê).
  String get operatorSchemaDir => '${_env['ProgramData'] ?? r'C:\ProgramData'}\\LasecFDT\\Schemas';

  String get _localDir => '${_env['LOCALAPPDATA'] ?? Directory.systemTemp.path}\\LasecFDT';

  String get logPath => '$_localDir\\atualizacao.log';

  /// Versão instalada: a do motor (igual à do MSI), ou a simulada no teste.
  String installedVersion(String hostVersion) => normalizeVersion(_testVersion ?? hostVersion) ?? '?';

  /// Consulta o GitHub. [hostVersion]: versão do motor (LasecFDT.Host), igual à do MSI instalado.
  Future<UpdateRelease?> findUpdate(String hostVersion) async {
    if (!enabled) return null;
    final current = _testVersion ?? hostVersion;
    final client = HttpClient()
      ..userAgent = _agent
      ..connectionTimeout = const Duration(seconds: 10);
    try {
      final req = await client.getUrl(_latest).timeout(const Duration(seconds: 10));
      req.headers
        ..set('Accept', 'application/vnd.github+json')
        ..set('X-GitHub-Api-Version', '2022-11-28');
      final res = await req.close().timeout(const Duration(seconds: 15));
      final body = await res.transform(utf8.decoder).join().timeout(const Duration(seconds: 15));
      if (res.statusCode != HttpStatus.ok) return null;
      final release = jsonDecode(body);
      if (release is! Map<String, dynamic> || release['draft'] == true || release['prerelease'] == true) return null;

      final tag = release['tag_name']?.toString() ?? '';
      final latest = normalizeVersion(tag);
      if (latest == null || compareVersions(latest, current) <= 0) return null;

      final assets = release['assets'];
      if (assets is! List) return null;
      for (final a in assets) {
        if (a is! Map<String, dynamic>) continue;
        final name = a['name']?.toString() ?? '';
        final url = Uri.tryParse(a['browser_download_url']?.toString() ?? '');
        if (!name.toLowerCase().endsWith('.msi') || name.toUpperCase().contains('USO-INTERNO')) continue;
        if (url == null || url.scheme != 'https' || url.host != 'github.com') continue;
        final digest = a['digest']?.toString();
        final page = Uri.tryParse(release['html_url']?.toString() ?? '');
        return UpdateRelease(
          tag: tag,
          version: latest,
          assetName: name,
          downloadUri: url,
          pageUri: page != null && page.scheme == 'https' && page.host == 'github.com'
              ? page
              : Uri.https('github.com', '/LASEC-UFU/LasecFDT/releases'),
          size: (a['size'] as num?)?.toInt() ?? 0,
          notes: _notes(release['body']?.toString() ?? ''),
          sha256: digest != null && digest.startsWith('sha256:') ? digest.substring(7).toLowerCase() : null,
        );
      }
      return null;
    } on Object {
      // Sem internet ou GitHub indisponível: não atrapalha a abertura do programa.
      return null;
    } finally {
      client.close(force: true);
    }
  }

  /// Baixa o MSI para %LOCALAPPDATA%\LasecFDT\Updates e confere o SHA-256 (quando o GitHub o informa).
  Future<String> download(UpdateRelease r, {required void Function(int received, int total) onProgress}) async {
    final dir = Directory('$_localDir\\Updates');
    await dir.create(recursive: true);
    final file = File('${dir.path}\\LasecFDT_${r.tag.replaceAll(RegExp(r'[^A-Za-z0-9._-]'), '_')}.msi');
    final client = HttpClient()
      ..userAgent = _agent
      ..connectionTimeout = const Duration(seconds: 20);
    try {
      final req = await client.getUrl(r.downloadUri).timeout(const Duration(seconds: 30));
      final res = await req.close().timeout(const Duration(seconds: 30));
      if (res.statusCode != HttpStatus.ok) {
        throw HttpException('O GitHub respondeu com o status ${res.statusCode}.', uri: r.downloadUri);
      }
      final total = res.contentLength > 0 ? res.contentLength : r.size;
      if (total > 300 * 1024 * 1024) throw const FormatException('O instalador excede o tamanho esperado.');
      final sink = file.openWrite();
      var received = 0;
      try {
        await for (final chunk in res.timeout(const Duration(seconds: 60))) {
          sink.add(chunk);
          received += chunk.length;
          onProgress(received, total);
        }
        await sink.flush();
      } finally {
        await sink.close();
      }
      if (received == 0 || (total > 0 && received != total)) {
        throw const HttpException('O download do instalador ficou incompleto.');
      }
      if (r.sha256 != null) {
        final actual = (await sha256.bind(file.openRead()).first).toString();
        if (actual != r.sha256) {
          throw const FormatException('A verificação SHA-256 do instalador falhou. O arquivo foi descartado.');
        }
      }
      return file.path;
    } catch (_) {
      if (await file.exists()) await file.delete();
      rethrow;
    } finally {
      client.close(force: true);
    }
  }

  /// Inicia o auxiliar que espera esta UI (e o motor/DTMs desta sessão) fechar, roda o MSI e reabre o LasecFDT.
  /// Retorna quando o auxiliar confirma que está rodando; aí a UI deve fechar.
  Future<void> startInstaller(String msiPath, {required String expectedVersion}) async {
    final d = installDir;
    if (d == null) throw const FileSystemException('A atualização automática só vale para a instalação do MSI.');
    final msi = File(msiPath);
    if (!await msi.exists() || await msi.length() == 0) {
      throw const FileSystemException('O instalador baixado não existe ou está vazio.');
    }
    final stamp = DateTime.now().microsecondsSinceEpoch;
    await Directory(_localDir).create(recursive: true);
    final readyPath = '$_localDir\\atualizacao_$stamp.ready';
    final helper = File('${Directory.systemTemp.path}\\lasecfdt_atualizador_${pid}_$stamp.ps1');
    await helper.writeAsBytes([0xef, 0xbb, 0xbf, ...utf8.encode(_helperScript)], flush: true);
    final windows = _env['WINDIR'] ?? r'C:\Windows';
    _log('Preparando a instalação da versão $expectedVersion ($msiPath).');
    Process? p;
    try {
      p = await Process.start('$windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe', [
        '-NoLogo', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
        '-File', helper.path,
        '-ParentPid', '$pid',
        '-MsiPath', msiPath,
        '-InstallDir', d,
        '-AppPath', '$d\\$_exe',
        '-ExpectedVersion', expectedVersion,
        '-LogPath', logPath,
        '-ReadyPath', readyPath,
      ]);
      // Modo normal com as saídas drenadas: no Windows o modo "detached" pode não executar o script (Gestor UFU).
      p.stdout.listen((_) {});
      p.stderr.listen((_) {});
      final deadline = DateTime.now().add(const Duration(seconds: 45));
      while (DateTime.now().isBefore(deadline)) {
        final ready = File(readyPath);
        if (await ready.exists()) {
          await ready.delete();
          _log('Auxiliar iniciado (PID ${p.pid}).');
          return;
        }
        await Future<void>.delayed(const Duration(milliseconds: 200));
      }
      _log('ERRO: o auxiliar não confirmou o início; o LasecFDT continua aberto.');
      p.kill();
      throw FileSystemException('O Windows não confirmou o início do instalador. Veja $logPath.');
    } on Object {
      if (p == null && await helper.exists()) await helper.delete();
      rethrow;
    }
  }

  void _log(String m) {
    try {
      final f = File(logPath);
      f.parent.createSync(recursive: true);
      f.writeAsStringSync('${DateTime.now().toIso8601String()} $m\r\n', mode: FileMode.append, flush: true);
    } catch (_) {}
  }

  /// "v0.1.2", "0.1.2.0", "0.1.2+77" → "0.1.2".
  static String? normalizeVersion(String value) {
    final parts = RegExp(r'\d+').allMatches(value).take(3).map((m) => int.tryParse(m.group(0)!)).toList();
    if (parts.length != 3 || parts.any((x) => x == null)) return null;
    return parts.join('.');
  }

  static int compareVersions(String a, String b) {
    final x = (normalizeVersion(a) ?? '0.0.0').split('.').map(int.parse).toList();
    final y = (normalizeVersion(b) ?? '0.0.0').split('.').map(int.parse).toList();
    for (var i = 0; i < 3; i++) {
      final c = x[i].compareTo(y[i]);
      if (c != 0) return c;
    }
    return 0;
  }

  /// Texto do Release (Markdown) legível como texto simples: sem **, #, crases nem sintaxe de link.
  static String plainNotes(String s) => s
      .replaceAllMapped(RegExp(r'\[([^\]]+)\]\((https?://[^)]+)\)'), (m) => m[1] == m[2] ? m[1]! : '${m[1]} (${m[2]})')
      .replaceAllMapped(RegExp(r'<(https?://[^>]+)>'), (m) => m[1]!)
      .replaceAll(RegExp(r'^[ \t]*#{1,6}[ \t]*', multiLine: true), '')
      .replaceAll(RegExp(r'^[ \t]*[-*][ \t]+', multiLine: true), '•  ')
      .replaceAll('**', '')
      .replaceAll('`', '')
      .replaceAll(RegExp(r'\n{3,}'), '\n\n');

  static String _notes(String s) {
    final t = plainNotes(s).trim();
    return t.length <= 1800 ? t : '${t.substring(0, 1800)}…';
  }

  static const _helperScript = r'''
param(
  [int]$ParentPid,
  [string]$MsiPath,
  [string]$InstallDir,
  [string]$AppPath,
  [string]$ExpectedVersion,
  [string]$LogPath,
  [string]$ReadyPath
)
$ErrorActionPreference = 'Stop'
$installed = $false
function Write-UpdateLog([string]$Message) {
  Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message" -Encoding UTF8
}
function Show-UpdateMessage([string]$Message, [bool]$IsError) {
  try {
    Add-Type -AssemblyName System.Windows.Forms
    $icon = if ($IsError) { [System.Windows.Forms.MessageBoxIcon]::Error } else { [System.Windows.Forms.MessageBoxIcon]::Information }
    [System.Windows.Forms.MessageBox]::Show("$Message`r`n`r`nLog: $LogPath", 'Atualização do LasecFDT',
      [System.Windows.Forms.MessageBoxButtons]::OK, $icon) | Out-Null
  } catch { }
}
function Get-Triple([string]$Value) {
  $n = @([regex]::Matches("$Value", '\d+') | Select-Object -First 3 | ForEach-Object { [int]$_.Value })
  if ($n.Count -lt 3) { return $null }
  return New-Object System.Version($n[0], $n[1], $n[2])
}
function Start-LasecFdt {
  if (Test-Path -LiteralPath $AppPath) { Start-Process -FilePath $AppPath -WorkingDirectory (Split-Path -Parent $AppPath) }
}
try {
  [System.IO.File]::WriteAllText($ReadyPath, [DateTime]::UtcNow.ToString('o'))
  Write-UpdateLog "Atualização para $ExpectedVersion iniciada. Instalador: $MsiPath"
  if (-not (Test-Path -LiteralPath $MsiPath)) { throw "O instalador baixado não foi encontrado: $MsiPath" }
  while (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }
  # O motor (LasecFDT.Host) e os workers dos DTMs desta sessão terminam logo depois da UI; espera até 60 s.
  $session = (Get-Process -Id $PID).SessionId
  $prefix = $InstallDir.TrimEnd('\') + '\'
  $deadline = (Get-Date).AddSeconds(60)
  do {
    $left = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
      $_.SessionId -eq $session -and $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($left.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  if ($left.Count -gt 0) {
    Write-UpdateLog "Encerrando processos que não fecharam: $(($left | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', ')"
    $left | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
  }
  Write-UpdateLog 'LasecFDT encerrado; iniciando o Windows Installer.'
  $msiLog = [System.IO.Path]::ChangeExtension($LogPath, 'msi.log')
  $arguments = '/i "' + $MsiPath + '" /passive /norestart /L*V "' + $msiLog + '"'
  try {
    $result = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
  } catch {
    Write-UpdateLog "Windows Installer não iniciado: $($_.Exception.Message)"
    Show-UpdateMessage 'A atualização foi cancelada (a permissão de administrador não foi concedida). O LasecFDT continua na versão atual.' $false
    Start-LasecFdt
    return
  }
  Write-UpdateLog "Windows Installer terminou com código $($result.ExitCode). Log MSI: $msiLog"
  if ($result.ExitCode -eq 1602) {
    Show-UpdateMessage 'A atualização foi cancelada. O LasecFDT continua na versão atual.' $false
    Start-LasecFdt
    return
  }
  if ($result.ExitCode -ne 0 -and $result.ExitCode -ne 3010) {
    throw "A instalação falhou com o código $($result.ExitCode)."
  }
  if (-not (Test-Path -LiteralPath $AppPath)) { throw "A instalação terminou, mas o programa não foi encontrado: $AppPath" }
  $version = (Get-Item -LiteralPath $AppPath).VersionInfo.ProductVersion
  $have = Get-Triple $version
  $want = Get-Triple $ExpectedVersion
  if ($null -eq $have -or $null -eq $want) { throw "Não foi possível conferir a versão instalada ($version; esperada $ExpectedVersion)." }
  if ($have -lt $want) { throw "A instalação terminou, mas a versão encontrada ($version) é anterior à esperada ($ExpectedVersion)." }
  Write-UpdateLog "Instalação confirmada: versão $version. Reabrindo o LasecFDT."
  if ($result.ExitCode -eq 3010) {
    Show-UpdateMessage "LasecFDT atualizado para $ExpectedVersion. O Windows pede para reiniciar o computador quando for possível." $false
  }
  Start-LasecFdt
  $installed = $true
} catch {
  $message = $_.Exception.Message
  try { Write-UpdateLog "ERRO: $message`r`n$($_ | Out-String)" } catch { }
  Show-UpdateMessage "Não foi possível concluir a atualização. $message" $true
  Start-LasecFdt
} finally {
  if ($installed) { Remove-Item -LiteralPath $MsiPath -Force -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
''';
}
