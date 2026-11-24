import 'dart:async';

import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../state/app_state.dart';
import 'widgets.dart';

/// Estação e planta: identidade desta estação, preferências (só desta estação) e os recursos que as estações do
/// servidor compartilham (quem está com qual porta e qual projeto).
class PlantPage extends StatefulWidget {
  const PlantPage({required this.state, super.key});
  final AppState state;

  @override
  State<PlantPage> createState() => _PlantPageState();
}

class _PlantPageState extends State<PlantPage> {
  List<Json> _leases = [];
  Timer? _timer;
  bool _wasVisible = false;

  @override
  void initState() {
    super.initState();
    _refresh();
    // Atualiza só quando a página está visível (IndexedStack mantém todas montadas).
    _timer = Timer.periodic(const Duration(seconds: 10), (_) {
      if (widget.state.page == AppPage.plant) _refresh();
    });
  }

  Future<void> _refresh() async {
    try {
      final l = await widget.state.leases();
      if (mounted) setState(() => _leases = l);
    } catch (_) {}
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.state;
    // Ao entrar na página, atualiza na hora (a lista pode ter sido lida quando outra operação estava em curso).
    final visible = s.page == AppPage.plant;
    if (visible && !_wasVisible) WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
    _wasVisible = visible;
    final st = s.station;
    final info = s.info;
    final stations = _leases.where((l) => '${l['key']}'.startsWith('station:')).toList();
    final resources = _leases.where((l) => !'${l['key']}'.startsWith('station:')).toList();
    final limits = (info['limits'] as Map?) ?? {};
    final reduced = s.settings['reducedEffects'];

    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 20),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Expanded(
          child: Column(children: [
            AppCard(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const SectionTitle('Esta estação'),
                KeyValue('Estação', '${st['id']}'),
                KeyValue('Thin client', (st['clientName'] as String?)?.isNotEmpty == true ? '${st['clientName']}' : 'sessão local (console)'),
                KeyValue('Sessão do Windows', '${st['sessionId']}${s.isRemote ? ' · remota' : ''}'),
                KeyValue('Usuário', '${st['user']}'),
                KeyValue('Servidor', '${st['machine']}'),
                KeyValue('Motor', 'LasecFDT.Host ${info['version']}'),
                KeyValue('Pasta da sessão', '${info['sessionDirectory']}', mono: true),
                KeyValue('Limites', 'até ${limits['maxWorkers']} DTMs carregados; ociosos descarregados após ${limits['idleUnloadMinutes']} min'),
              ]),
            ),
            const SizedBox(height: 14),
            AppCard(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const SectionTitle('Preferências desta estação'),
                Row(children: [
                  SizedBox(width: 190, child: Text('Tema', style: TextStyle(color: AppTheme.textSecondary))),
                  SegmentedButton<String>(
                    segments: const [
                      ButtonSegment(value: 'dark', icon: Icon(Icons.dark_mode_rounded), label: Text('Escuro')),
                      ButtonSegment(value: 'light', icon: Icon(Icons.light_mode_rounded), label: Text('Claro')),
                    ],
                    selected: {s.isLight ? 'light' : 'dark'},
                    onSelectionChanged: (v) => s.updateSettings({'theme': v.first}),
                  ),
                ]),
                const SizedBox(height: 12),
                Row(children: [
                  SizedBox(width: 190, child: Text('Efeitos visuais', style: TextStyle(color: AppTheme.textSecondary))),
                  SegmentedButton<String>(
                    segments: const [
                      ButtonSegment(value: 'auto', label: Text('Automático')),
                      ButtonSegment(value: 'reduced', label: Text('Reduzidos')),
                      ButtonSegment(value: 'full', label: Text('Completos')),
                    ],
                    selected: {reduced == null ? 'auto' : (reduced == true ? 'reduced' : 'full')},
                    onSelectionChanged: (v) => s.updateSettings({'reducedEffects': v.first == 'auto' ? null : v.first == 'reduced'}),
                  ),
                ]),
                Padding(
                  padding: const EdgeInsets.only(left: 190, top: 4),
                  child: Text('Automático = reduzidos em sessão remota (thin client): menos CPU no servidor e menos banda.',
                      style: TextStyle(fontSize: 12, color: AppTheme.textSecondary)),
                ),
                const SizedBox(height: 12),
                Row(children: [
                  SizedBox(width: 190, child: Text('Idioma pedido aos DTMs', style: TextStyle(color: AppTheme.textSecondary))),
                  SizedBox(
                    width: 220,
                    child: DropdownButtonFormField<String>(
                      initialValue: '${s.settings['language'] ?? 'pt-BR'}',
                      items: const [
                        DropdownMenuItem(value: 'pt-BR', child: Text('Português (Brasil)')),
                        DropdownMenuItem(value: 'en-US', child: Text('English')),
                        DropdownMenuItem(value: 'de-DE', child: Text('Deutsch')),
                      ],
                      onChanged: (v) => s.updateSettings({'language': v}),
                    ),
                  ),
                ]),
                Padding(
                  padding: const EdgeInsets.only(left: 190, top: 4),
                  child: Text('Vale para os DTMs carregados a partir de agora (o DTM usa inglês se não tiver o idioma).',
                      style: TextStyle(fontSize: 12, color: AppTheme.textSecondary)),
                ),
              ]),
            ),
          ]),
        ),
        const SizedBox(width: 14),
        Expanded(
          child: Column(children: [
            AppCard(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                SectionTitle('Planta',
                    trailing: IconButton(tooltip: 'Atualizar', onPressed: _refresh, icon: Icon(Icons.refresh_rounded, color: AppTheme.textSecondary))),
                KeyValue('Pasta comum', '${info['plantDirectory']}', mono: true),
                KeyValue('Compartilhada', info['plantShared'] == true ? 'sim — projetos, catálogo e bloqueios valem para todas as estações' : 'NÃO — modo isolado'),
                const SizedBox(height: 10),
                Text('Estações ativas (${stations.length})', style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.text)),
                const SizedBox(height: 6),
                for (final l in stations)
                  _LeaseRow(
                    icon: Icons.computer_rounded,
                    title: '${l['station']}',
                    subtitle: '${l['user']} · sessão ${l['sessionId']} · desde ${_time(l['sinceUtc'])}',
                    mine: l['mine'] == true,
                  ),
              ]),
            ),
            const SizedBox(height: 14),
            AppCard(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const SectionTitle('Recursos em uso'),
                if (resources.isEmpty) Text('Nenhuma porta ou projeto em uso no momento.', style: TextStyle(color: AppTheme.textSecondary)),
                for (final l in resources)
                  _LeaseRow(
                    icon: '${l['key']}'.startsWith('port:') ? Icons.usb_rounded : Icons.account_tree_rounded,
                    title: _resource('${l['key']}'),
                    subtitle: 'estação ${l['station']} · ${l['user']} · desde ${_time(l['sinceUtc'])}${l['detail'] != null ? ' · ${l['detail']}' : ''}',
                    mine: l['mine'] == true,
                  ),
              ]),
            ),
          ]),
        ),
      ]),
    );
  }

  static String _resource(String key) {
    final parts = key.split(':');
    return switch (parts.first) {
      'port' => 'Porta ${parts.last}${parts.length > 2 ? ' (${parts[1]})' : ''}',
      'project' => 'Projeto ${parts.sublist(1).join(':')}',
      _ => key,
    };
  }

  static String _time(dynamic iso) {
    final d = DateTime.tryParse('$iso')?.toLocal();
    return d == null ? '?' : '${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';
  }
}

class _LeaseRow extends StatelessWidget {
  const _LeaseRow({required this.icon, required this.title, required this.subtitle, required this.mine});
  final IconData icon;
  final String title;
  final String subtitle;
  final bool mine;

  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 6),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(color: AppTheme.cardAlt, borderRadius: BorderRadius.circular(10)),
        child: Row(children: [
          Icon(icon, size: 18, color: mine ? AppTheme.primary : AppTheme.textSecondary),
          const SizedBox(width: 10),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(title, style: TextStyle(fontWeight: FontWeight.w600, color: AppTheme.text, fontSize: 13)),
              Text(subtitle, style: TextStyle(fontSize: 11.5, color: AppTheme.textSecondary)),
            ]),
          ),
          if (mine) Pill('esta estação', color: AppTheme.primary),
        ]),
      );
}
