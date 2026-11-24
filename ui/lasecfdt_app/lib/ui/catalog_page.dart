import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../state/app_state.dart';
import 'widgets.dart';

/// Catálogo de DTMs do servidor (como o "Catálogo de dispositivos" do PACTware): busca, filtro por papel e
/// fabricante, tipos de dispositivo. É calculado uma vez por servidor e compartilhado por todas as estações.
class CatalogPage extends StatefulWidget {
  const CatalogPage({required this.state, super.key});
  final AppState state;

  @override
  State<CatalogPage> createState() => _CatalogPageState();
}

class _CatalogPageState extends State<CatalogPage> {
  String _query = '';
  String _role = 'all';
  String? _vendor;
  (int, int)? _progress;

  Future<void> _updateAll() async {
    setState(() => _progress = (0, 0));
    await widget.state.describeAll((d, t) {
      if (mounted) setState(() => _progress = (d, t));
    });
    if (mounted) setState(() => _progress = null);
    widget.state.toast('Catálogo atualizado para todas as estações.');
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.state;
    final vendors = s.catalog.map((e) => '${e['vendor'] ?? '—'}').toSet().toList()..sort();
    final list = s.catalog.where((e) {
      if (_role != 'all' && (_role == 'comm' ? e['role'] == 'Device' : e['role'] != 'Device')) return false;
      if (_vendor != null && '${e['vendor'] ?? '—'}' != _vendor) return false;
      final q = _query.toLowerCase();
      return q.isEmpty || '${e['name']} ${e['vendor']} ${e['progId']} ${(e['fieldbuses'] as List?)?.join(' ')}'.toLowerCase().contains(q);
    }).toList();
    final meta = s.catalogMeta;

    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 20),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Row(children: [
          SizedBox(
            width: 340,
            child: TextField(
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search_rounded), hintText: 'Buscar DTM, fabricante, protocolo…'),
              onChanged: (v) => setState(() => _query = v),
            ),
          ),
          const SizedBox(width: 12),
          ChoiceChip(label: const Text('Todos'), selected: _role == 'all', onSelected: (_) => setState(() => _role = 'all')),
          const SizedBox(width: 6),
          ChoiceChip(label: const Text('Comunicação/gateway'), selected: _role == 'comm', onSelected: (_) => setState(() => _role = 'comm')),
          const SizedBox(width: 6),
          ChoiceChip(label: const Text('Dispositivos'), selected: _role == 'dev', onSelected: (_) => setState(() => _role = 'dev')),
          const SizedBox(width: 12),
          SizedBox(
            width: 230,
            child: DropdownButtonFormField<String?>(
              initialValue: _vendor,
              isExpanded: true,
              decoration: const InputDecoration(labelText: 'Fabricante'),
              items: [
                const DropdownMenuItem(value: null, child: Text('Todos')),
                for (final v in vendors) DropdownMenuItem(value: v, child: Text(v, overflow: TextOverflow.ellipsis)),
              ],
              onChanged: (v) => setState(() => _vendor = v),
            ),
          ),
          const Spacer(),
          if (_progress != null) ...[
            busySpinner(),
            const SizedBox(width: 8),
            Text(_progress!.$2 == 0 ? 'Lendo o registro…' : 'Lendo DTMs ${_progress!.$1}/${_progress!.$2}…', style: TextStyle(color: AppTheme.textSecondary)),
            const SizedBox(width: 12),
          ],
          ElevatedButton.icon(
            onPressed: _progress != null || s.catalogLoading ? null : _updateAll,
            icon: const Icon(Icons.sync_rounded, size: 18),
            label: const Text('Atualizar catálogo'),
          ),
        ]),
        const SizedBox(height: 8),
        if (meta != null)
          Text(
            '${list.length} de ${s.catalog.length} DTMs · gerado ${meta['fromCache'] == true ? 'pela estação ${meta['generatedBy'] ?? '?'} (cache da planta)' : 'agora'}'
            ' · os DTMs sem tipos de dispositivo ainda não foram lidos: "Atualizar catálogo" lê todos uma vez para o servidor.',
            style: TextStyle(color: AppTheme.textSecondary, fontSize: 12.5),
          ),
        const SizedBox(height: 12),
        Expanded(
          child: s.catalogLoading && s.catalog.isEmpty
              ? Center(child: busySpinner(size: 26))
              : GridView.builder(
                  gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(maxCrossAxisExtent: 430, mainAxisExtent: 168, crossAxisSpacing: 12, mainAxisSpacing: 12),
                  itemCount: list.length,
                  itemBuilder: (_, i) => _DtmCard(state: s, e: list[i]),
                ),
        ),
      ]),
    );
  }
}

class _DtmCard extends StatelessWidget {
  const _DtmCard({required this.state, required this.e});
  final AppState state;
  final Json e;

  @override
  Widget build(BuildContext context) {
    final types = (e['deviceTypes'] as List?)?.cast<Map>();
    final issues = ((e['issues'] as List?) ?? []).cast<String>();
    final buses = ((e['fieldbuses'] as List?) ?? []).cast<String>();
    final hostable = e['hostable'] == true;
    return AppCard(
      padding: const EdgeInsets.all(14),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          IconBadge(roleIcon(e['role'] as String?), color: roleColor(e['role'] as String?), size: 20),
          const SizedBox(width: 12),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${e['name']}', overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.text)),
              Text('${e['vendor'] ?? '—'} · v${e['version'] ?? '?'}', overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: AppTheme.textSecondary)),
            ]),
          ),
        ]),
        const SizedBox(height: 10),
        Wrap(spacing: 6, runSpacing: 6, children: [
          Pill(roleLabel(e['role'] as String?), color: roleColor(e['role'] as String?)),
          for (final b in buses) Pill(b, color: AppTheme.accent),
          if (!hostable) Pill('não hospedável', color: AppTheme.danger, icon: Icons.block_rounded),
        ]),
        const Spacer(),
        Row(children: [
          Expanded(
            child: Text(
              types == null
                  ? (issues.isNotEmpty ? issues.first : 'Tipos de dispositivo ainda não lidos')
                  : types.map((t) => t['name']).join(', '),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontSize: 12, color: AppTheme.textSecondary),
            ),
          ),
          if (types == null && hostable)
            TextButton(onPressed: () => state.describe(e['clsid'] as String), child: const Text('Ler DTM')),
        ]),
      ]),
    );
  }
}
