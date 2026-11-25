import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../core/native_dock.dart';
import '../core/update_service.dart';
import '../state/app_state.dart';

/// Ao abrir: consulta o GitHub e, se houver versão nova, pergunta se pode atualizar (como no Gestor UFU).
/// Silencioso quando não há versão nova, sem internet ou fora da instalação do MSI.
Future<void> checkForUpdates(BuildContext Function() context, AppState state, {UpdateService? service}) async {
  final svc = service ?? UpdateService();
  if (!svc.enabled) return;
  final release = await svc.findUpdate('${state.info['version'] ?? ''}');
  if (release == null) return;
  AppState.log('atualização: ${release.version} disponível (${release.assetName})');
  var ctx = context();
  if (!ctx.mounted) return;
  final go = await showDialog<bool>(context: ctx, builder: (_) => _OfferDialog(state: state, service: svc, release: release));
  if (go != true) return;
  ctx = context();
  if (!ctx.mounted) return;
  await showDialog<void>(
    context: ctx,
    barrierDismissible: false,
    builder: (_) => _InstallDialog(state: state, service: svc, release: release),
  );
}

void _openPage(Uri uri) {
  // Abre no navegador padrão (só endereços https do github.com chegam aqui).
  unawaited(Process.start('rundll32.exe', ['url.dll,FileProtocolHandler', uri.toString()]).then((_) {}, onError: (_) {}));
}

String _mb(int bytes) => (bytes / (1024 * 1024)).toStringAsFixed(1);

class _OfferDialog extends StatefulWidget {
  const _OfferDialog({required this.state, required this.service, required this.release});
  final AppState state;
  final UpdateService service;
  final UpdateRelease release;

  @override
  State<_OfferDialog> createState() => _OfferDialogState();
}

class _OfferDialogState extends State<_OfferDialog> {
  /// Outras estações do servidor com o LasecFDT aberto: o Windows Installer não troca arquivos em uso.
  List<Json>? _others;
  bool _checking = false;

  @override
  void initState() {
    super.initState();
    _checkStations();
  }

  Future<void> _checkStations() async {
    setState(() => _checking = true);
    List<Json> others;
    try {
      others = (await widget.state.leases())
          .where((l) => '${l['key']}'.startsWith('station:') && l['mine'] != true)
          .toList();
    } catch (_) {
      others = const [];
    }
    if (!mounted) return;
    setState(() {
      _others = others;
      _checking = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.state;
    final r = widget.release;
    final others = _others ?? const <Json>[];
    final current = widget.service.installedVersion('${s.info['version'] ?? ''}');
    final blocked = _others == null || others.isNotEmpty || s.working;
    final muted = TextStyle(color: AppTheme.textSecondary, height: 1.4);
    return AlertDialog(
      icon: Icon(Icons.system_update_alt_rounded, size: 34, color: AppTheme.primary),
      title: Text('Nova versão ${r.version} disponível'),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Instalada: $current. O LasecFDT fecha durante a instalação (o Windows pede permissão de '
                  'administrador) e abre de novo ao final. Projetos, preferências e a pasta da planta são mantidos.',
                  style: muted),
              if (s.projectDirty) ...[
                const SizedBox(height: 12),
                _Note(icon: Icons.save_outlined, color: AppTheme.warning,
                    text: 'O projeto aberto tem alterações não salvas. Ele será salvo antes de fechar.'),
              ],
              if (widget.service.isInternal) ...[
                const SizedBox(height: 12),
                _Note(icon: Icons.schema_outlined, color: AppTheme.warning,
                    text: 'Esta instalação é de USO INTERNO e tem os schemas HART da FDT 1.2. O pacote do GitHub não '
                        'os traz: antes de remover a versão atual, o instalador copia esses schemas para '
                        '${widget.service.operatorSchemaDir} (sem apagar nem sobrescrever o que já estiver lá), onde '
                        'continuam valendo para todas as estações.'),
              ],
              if (s.working) ...[
                const SizedBox(height: 12),
                _Note(icon: Icons.hourglass_top_rounded, color: AppTheme.warning,
                    text: 'Há uma operação em andamento nesta estação. Espere terminar ou use Parar.'),
              ],
              if (_others == null) ...[
                const SizedBox(height: 12),
                const LinearProgressIndicator(),
              ] else if (others.isNotEmpty) ...[
                const SizedBox(height: 12),
                _Note(
                  icon: Icons.desktop_windows_outlined,
                  color: AppTheme.danger,
                  text: 'Outras estações estão com o LasecFDT aberto e usam os mesmos arquivos do programa. '
                      'Peça para fecharem e clique em "Verificar de novo":\n'
                      '${others.map((l) => '•  ${l['station']} (${l['user']})').join('\n')}',
                ),
              ],
              if (r.notes.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text('Novidades', style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 6),
                ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 220),
                  child: Container(
                    width: double.infinity,
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(color: AppTheme.cardAlt, borderRadius: BorderRadius.circular(8)),
                    child: SingleChildScrollView(child: SelectableText(r.notes, style: muted.copyWith(fontSize: 12))),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: () => _openPage(r.pageUri), child: const Text('Ver no GitHub')),
        if (others.isNotEmpty || s.working)
          TextButton(onPressed: _checking ? null : _checkStations, child: const Text('Verificar de novo')),
        TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Agora não')),
        ElevatedButton.icon(
          onPressed: blocked ? null : () => Navigator.pop(context, true),
          icon: const Icon(Icons.download_rounded, size: 18),
          label: const Text('Atualizar'),
        ),
      ],
    );
  }
}

class _Note extends StatelessWidget {
  const _Note({required this.icon, required this.color, required this.text});
  final IconData icon;
  final Color color;
  final String text;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.all(10),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: color.withValues(alpha: 0.4)),
        ),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(width: 8),
          Expanded(child: Text(text, style: TextStyle(color: AppTheme.text, height: 1.4))),
        ]),
      );
}

class _InstallDialog extends StatefulWidget {
  const _InstallDialog({required this.state, required this.service, required this.release});
  final AppState state;
  final UpdateService service;
  final UpdateRelease release;

  @override
  State<_InstallDialog> createState() => _InstallDialogState();
}

class _InstallDialogState extends State<_InstallDialog> {
  int _received = 0;
  int _total = 0;
  bool _busy = false;
  String _step = '';
  String? _error;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => unawaited(_run()));
  }

  Future<void> _run() async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _received = 0;
      _total = 0;
      _step = 'Baixando';
    });
    final s = widget.state;
    try {
      final msi = await widget.service.download(widget.release, onProgress: (received, total) {
        if (!mounted) return;
        setState(() {
          _received = received;
          _total = total;
        });
      });
      if (s.projectDirty) {
        setState(() => _step = 'Salvando o projeto');
        if (!await s.saveProject()) throw StateError('o projeto não foi salvo; a atualização foi interrompida.');
      }
      setState(() => _step = 'Iniciando o instalador');
      await widget.service.startInstaller(msi, expectedVersion: widget.release.version);
      // Fecha como no X: o Host interrompe o que houver e libera os DTMs; o auxiliar espera e roda o MSI.
      setState(() => _step = 'Fechando o LasecFDT');
      s.beginClose();
      unawaited(NativeDock.instance.overlayOpened());
      await s.host.stop(wait: const Duration(seconds: 20));
      exit(0);
    } on Object catch (e) {
      AppState.log('atualização: ERRO $e');
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = 'Não foi possível atualizar: $e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final r = widget.release;
    final downloading = _step == 'Baixando';
    final progress = downloading && _total > 0 ? (_received / _total).clamp(0.0, 1.0) : null;
    return AlertDialog(
      icon: Icon(_error == null ? Icons.downloading_rounded : Icons.error_outline,
          size: 34, color: _error == null ? AppTheme.primary : AppTheme.danger),
      title: Text(_error == null ? 'Atualizando para ${r.version}' : 'Falha na atualização'),
      content: SizedBox(
        width: 460,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              _error ??
                  'Depois do download, o LasecFDT fecha, o Windows Installer substitui a versão atual e o '
                      'LasecFDT abre de novo.',
              style: TextStyle(color: AppTheme.textSecondary, height: 1.4),
            ),
            if (_error == null) ...[
              const SizedBox(height: 18),
              LinearProgressIndicator(value: progress),
              const SizedBox(height: 8),
              Text(downloading
                  ? (_total > 0 ? '${_mb(_received)} MB de ${_mb(_total)} MB' : '${_mb(_received)} MB baixados')
                  : '$_step…'),
            ],
          ],
        ),
      ),
      actions: _error == null
          ? null
          : [
              TextButton(onPressed: () => Navigator.pop(context), child: const Text('Fechar')),
              ElevatedButton.icon(
                onPressed: _busy ? null : _run,
                icon: const Icon(Icons.refresh_rounded, size: 18),
                label: const Text('Tentar novamente'),
              ),
            ],
    );
  }
}
