import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../state/app_state.dart';
import 'widgets.dart';

/// Eventos desta estação (como a janela de mensagens do PACTware): Frame/DTM, comunicação, telas, workers,
/// projeto e planta. Nada de outras estações aparece aqui.
class EventsPage extends StatefulWidget {
  const EventsPage({required this.state, super.key});
  final AppState state;

  @override
  State<EventsPage> createState() => _EventsPageState();
}

class _EventsPageState extends State<EventsPage> {
  final _kinds = <String>{'frame', 'ui', 'worker', 'plant', 'project', 'host'};
  String _query = '';
  bool _onlyProblems = false;

  static const _labels = {
    'frame': 'DTM / Frame',
    'comm': 'Comunicação',
    'ui': 'Telas',
    'worker': 'Processos',
    'plant': 'Planta',
    'project': 'Projeto',
    'host': 'Motor',
  };

  @override
  Widget build(BuildContext context) {
    final s = widget.state;
    final q = _query.toLowerCase();
    final list = s.events.where((e) {
      if (!_kinds.contains(e['kind'])) return false;
      if (_onlyProblems && e['level'] == 'info') return false;
      if (q.isEmpty) return true;
      final name = s.nodes[e['tag']]?['name'] ?? '';
      return '$name ${e['method']} ${e['detail']}'.toLowerCase().contains(q);
    }).toList();

    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 20),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Wrap(spacing: 6, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
          SizedBox(
            width: 300,
            child: TextField(
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search_rounded), hintText: 'Filtrar…'),
              onChanged: (v) => setState(() => _query = v),
            ),
          ),
          const SizedBox(width: 6),
          for (final k in _labels.keys)
            FilterChip(
              label: Text(_labels[k]!),
              selected: _kinds.contains(k),
              onSelected: (v) => setState(() => v ? _kinds.add(k) : _kinds.remove(k)),
            ),
          const SizedBox(width: 6),
          FilterChip(label: const Text('Só avisos e erros'), selected: _onlyProblems, onSelected: (v) => setState(() => _onlyProblems = v)),
        ]),
        const SizedBox(height: 12),
        Expanded(
          child: AppCard(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: list.isEmpty
                ? EmptyState(icon: Icons.receipt_long_rounded, title: 'Sem eventos', message: 'Os eventos aparecem aqui conforme os DTMs trabalham.')
                : ListView.builder(
                    itemCount: list.length,
                    itemBuilder: (_, i) {
                      final e = list[i];
                      final level = e['level'] as String? ?? 'info';
                      final color = level == 'error' ? AppTheme.danger : level == 'warning' ? AppTheme.warning : AppTheme.textSecondary;
                      final time = DateTime.tryParse('${e['time']}');
                      final node = s.nodes[e['tag']];
                      return Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
                        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                          Icon(level == 'info' ? Icons.circle : Icons.warning_amber_rounded, size: level == 'info' ? 8 : 15, color: color),
                          const SizedBox(width: 10),
                          SizedBox(
                            width: 92,
                            child: Text(time == null ? '' : '${time.hour.toString().padLeft(2, '0')}:${time.minute.toString().padLeft(2, '0')}:${time.second.toString().padLeft(2, '0')}',
                                style: TextStyle(fontFamily: 'Consolas', fontSize: 12, color: AppTheme.textSecondary)),
                          ),
                          SizedBox(width: 110, child: Text(_labels[e['kind']] ?? '${e['kind']}', style: TextStyle(fontSize: 12, color: AppTheme.textSecondary))),
                          SizedBox(
                            width: 180,
                            child: Text('${node?['name'] ?? e['tag'] ?? ''}',
                                overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: AppTheme.text)),
                          ),
                          Expanded(
                            child: SelectableText('${e['method'] ?? ''} ${e['detail'] ?? ''}'.trim(),
                                style: TextStyle(fontSize: 12.5, color: level == 'info' ? AppTheme.text : color)),
                          ),
                        ]),
                      );
                    },
                  ),
          ),
        ),
      ]),
    );
  }
}
