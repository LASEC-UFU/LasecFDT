import 'dart:async';

import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../core/host_client.dart';
import '../state/app_state.dart';
import 'project_page.dart' show StopButton;
import 'widgets.dart';

/// Busca de instrumentos no canal de um Comm DTM/gateway (como a busca de topologia do PACTware): o Comm DTM varre
/// os endereços só com comandos de identificação; cada instrumento volta com os DTMs candidatos do catálogo.
Future<void> showScanDialog(BuildContext context, AppState s, Json node) =>
    showDialog(context: context, barrierDismissible: false, builder: (_) => _ScanDialog(state: s, node: node));

class _Row {
  _Row(this.device) : selected = device['existingNodeTag'] == null && (device['candidates'] as List).isNotEmpty;
  final Json device;
  bool selected;
  int candidate = 0;
  List<Json> get candidates => (device['candidates'] as List).map((e) => Map<String, dynamic>.from(e as Map)).toList();
}

class _ScanDialog extends StatefulWidget {
  const _ScanDialog({required this.state, required this.node});
  final AppState state;
  final Json node;

  @override
  State<_ScanDialog> createState() => _ScanDialogState();
}

class _ScanDialogState extends State<_ScanDialog> {
  String _progress = 'Preparando…';
  String? _error;
  bool _canceled = false;
  List<_Row>? _rows;
  Json? _result;
  bool _adding = false;
  StreamSubscription<HostNotification>? _sub;
  final _sw = Stopwatch()..start();
  Timer? _tick;

  String get _tag => widget.node['tag'] as String;
  bool get _running => _rows == null && _error == null;

  @override
  void initState() {
    super.initState();
    // Progresso: leitura de DTMs do catálogo (1ª vez no servidor) e OnProgress do Comm DTM.
    _sub = widget.state.host.notifications.listen((n) {
      if (n.method != 'event' || !mounted) return;
      final e = n.params;
      if (e['tag'] != _tag) return;
      if (e['kind'] == 'catalog' || e['method'] == 'OnProgress' || e['method'] == 'scanStarted') {
        setState(() => _progress = '${e['detail'] ?? ''}'.replaceAll(RegExp(r' show=\w+$'), ''));
      }
    });
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted && _rows == null && _error == null) setState(() {});
    });
    _run();
  }

  Future<void> _run() async {
    setState(() {
      _error = null;
      _canceled = false;
      _rows = null;
      _progress = 'Preparando…';
    });
    _sw.reset();
    try {
      final r = await widget.state.scan(_tag);
      if (!mounted) return;
      setState(() {
        _result = r;
        _rows = ((r['devices'] as List?) ?? []).map((d) => _Row(Map<String, dynamic>.from(d as Map))).toList();
      });
    } catch (e) {
      if (mounted) {
        setState(() {
          _canceled = AppState.isCanceled(e);
          _error = e.toString();
        });
      }
    }
  }

  Future<void> _add() async {
    setState(() => _adding = true);
    var ok = 0;
    for (final r in _rows!.where((r) => r.selected)) {
      final c = r.candidates[r.candidate];
      if (await widget.state.addNode(
        clsid: c['clsid'] as String,
        deviceTypeIndex: (c['deviceTypeIndex'] as num?)?.toInt() ?? 0,
        parentTag: _tag,
        name: '${r.device['deviceTag']}',
      )) {
        ok++;
      }
    }
    if (!mounted) return;
    Navigator.pop(context);
    widget.state.toast('$ok instrumento(s) adicionado(s) em ${widget.node['name']}.');
  }

  @override
  void dispose() {
    _sub?.cancel();
    _tick?.cancel();
    super.dispose();
  }

  static String _hex(dynamic v) => v == null ? '?' : '0x${(v as num).toInt().toRadixString(16).toUpperCase().padLeft(2, '0')} ($v)';

  @override
  Widget build(BuildContext context) {
    final rows = _rows;
    final selected = rows?.where((r) => r.selected).length ?? 0;
    return AlertDialog(
      title: Row(children: [
        Icon(Icons.radar_rounded, color: AppTheme.primary),
        const SizedBox(width: 10),
        Expanded(child: Text('Buscar instrumentos · ${widget.node['name']}')),
      ]),
      content: SizedBox(
        width: 980,
        height: 480,
        child: _error != null
            ? EmptyState(
                icon: _canceled ? Icons.stop_circle_outlined : Icons.error_outline_rounded,
                title: _canceled ? 'Busca interrompida' : 'A busca não foi concluída',
                message: _canceled ? 'A busca foi interrompida e a porta liberada. Nada mudou no projeto.' : _error,
                action: OutlinedButton.icon(onPressed: _run, icon: const Icon(Icons.refresh_rounded), label: const Text('Tentar de novo')),
              )
            : rows == null
                ? EmptyState(
                    icon: Icons.radar_rounded,
                    title: 'Buscando… ${_sw.elapsed.inSeconds} s',
                    message: '$_progress\n\nSomente leitura: o DTM de comunicação consulta cada endereço com comandos de identificação. '
                        'A porta fica reservada para esta estação durante a busca.',
                    action: busySpinner(size: 26),
                  )
                : rows.isEmpty
                    ? EmptyState(
                        icon: Icons.search_off_rounded,
                        title: 'Nenhum instrumento respondeu',
                        message: 'Confira o modem/gateway, a porta, a faixa de endereços da busca na configuração do DTM de comunicação e se o laço está alimentado.',
                        action: OutlinedButton.icon(onPressed: _run, icon: const Icon(Icons.refresh_rounded), label: const Text('Buscar de novo')),
                      )
                    : Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                        Text(
                          '${rows.length} instrumento(s) em ${((_result?['durationMs'] as num? ?? 0) / 1000).toStringAsFixed(1)} s no canal ${_result?['channelPath']}. '
                          'Escolha o DTM de cada um e adicione ao projeto.',
                          style: TextStyle(color: AppTheme.textSecondary),
                        ),
                        const SizedBox(height: 10),
                        Expanded(
                          child: ListView.separated(
                            itemCount: rows.length,
                            separatorBuilder: (_, _) => const SizedBox(height: 6),
                            itemBuilder: (_, i) => _deviceRow(rows[i]),
                          ),
                        ),
                      ]),
      ),
      actions: [
        // Fechar no meio da busca também a interrompe (antes, ela seguia escondida e segurava a fila do projeto).
        TextButton(
          onPressed: _adding
              ? null
              : () {
                  if (_running) unawaited(widget.state.stopAll());
                  Navigator.pop(context);
                },
          child: const Text('Fechar'),
        ),
        if (_running) ListenableBuilder(listenable: widget.state, builder: (_, _) => StopButton(state: widget.state, label: 'Parar busca')),
        if (rows != null && rows.isNotEmpty)
          ElevatedButton.icon(
            onPressed: selected == 0 || _adding || widget.state.projectReadOnly ? null : _add,
            icon: _adding ? busySpinner(color: Colors.white) : const Icon(Icons.add_rounded, size: 18),
            label: Text('Adicionar selecionados ($selected)'),
          ),
      ],
    );
  }

  Widget _deviceRow(_Row r) {
    final d = r.device;
    final cands = r.candidates;
    final existing = d['existingNodeTag'] != null;
    String matchLabel(String? m) => switch (m) {
          'exact' => 'mesmo fabricante e tipo',
          'vendor' => 'mesmo fabricante',
          _ => 'mesmo protocolo',
        };
    Color matchColor(String? m) => switch (m) {
          'exact' => AppTheme.green,
          'vendor' => AppTheme.warning,
          _ => AppTheme.textSecondary,
        };
    final best = cands.isEmpty ? null : cands[r.candidate];
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(color: AppTheme.cardAlt, borderRadius: BorderRadius.circular(12)),
      child: Row(children: [
        Checkbox(
          value: r.selected,
          onChanged: cands.isEmpty || existing ? null : (v) => setState(() => r.selected = v ?? false),
        ),
        Icon(Icons.sensors_rounded, color: AppTheme.primary),
        const SizedBox(width: 10),
        SizedBox(
          width: 250,
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Flexible(child: Text('${d['deviceTag']}', overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.text))),
              if (existing) ...[const SizedBox(width: 8), Pill('já no projeto', color: AppTheme.accent)],
            ]),
            Text(
              '${d['protocol']} · endereço ${d['shortAddress'] ?? '—'}${d['longAddress'] != null ? ' · ${d['longAddress']}' : ''}',
              style: TextStyle(fontSize: 12, color: AppTheme.textSecondary, fontFamily: 'Consolas'),
            ),
            Text(
              'Fabricante ${d['manufacturerName'] ?? _hex(d['manufacturerId'])} · tipo ${_hex(d['deviceTypeId'])}',
              style: TextStyle(fontSize: 12, color: AppTheme.textSecondary),
            ),
          ]),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: cands.isEmpty
              ? Text('Nenhum DTM instalado para este protocolo.', style: TextStyle(color: AppTheme.danger))
              : DropdownButtonFormField<int>(
                  initialValue: r.candidate,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'DTM'),
                  items: [
                    for (var k = 0; k < cands.length; k++)
                      DropdownMenuItem(
                        value: k,
                        child: Text('${cands[k]['deviceTypeName']} — ${cands[k]['vendor'] ?? ''} ${cands[k]['name']}', overflow: TextOverflow.ellipsis),
                      ),
                  ],
                  onChanged: existing ? null : (v) => setState(() => r.candidate = v ?? 0),
                ),
        ),
        const SizedBox(width: 10),
        if (best != null) Pill(matchLabel(best['match'] as String?), color: matchColor(best['match'] as String?)),
      ]),
    );
  }
}
