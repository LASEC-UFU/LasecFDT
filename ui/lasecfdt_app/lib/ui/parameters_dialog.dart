import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../core/host_client.dart';
import '../state/app_state.dart';
import 'widgets.dart';

/// Parâmetros do DTM (IDtmParameter::GetParameters), com busca. Somente leitura.
Future<void> showParametersDialog(BuildContext context, AppState s, Json node) async {
  Json? data;
  try {
    data = await s.parameters(node['tag'] as String);
  } catch (e) {
    s.toast('Parâmetros: $e', error: true);
    return;
  }
  if (!context.mounted) return;
  final items = ((data['items'] as List?) ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList();
  var query = '';
  await showDialog(
    context: context,
    builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
      final list = items.where((i) => query.isEmpty || '${i['path']} ${i['value']}'.toLowerCase().contains(query.toLowerCase())).toList();
      return AlertDialog(
        title: Text('Parâmetros · ${node['name']}'),
        content: SizedBox(
          width: 860,
          height: 520,
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Row(children: [
              SizedBox(
                width: 320,
                child: TextField(
                  autofocus: true,
                  decoration: const InputDecoration(prefixIcon: Icon(Icons.search_rounded), hintText: 'Filtrar parâmetros…'),
                  onChanged: (v) => set(() => query = v),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  '${items.length} parâmetro(s) publicados pelo DTM · dados ${data!['storageState'] ?? '?'} / ${data['dataSetState'] ?? '?'}',
                  style: TextStyle(color: AppTheme.textSecondary, fontSize: 12.5),
                ),
              ),
            ]),
            const SizedBox(height: 10),
            Expanded(
              child: items.isEmpty
                  ? EmptyState(icon: Icons.tune_rounded, title: 'O DTM não publicou parâmetros', message: 'Use a tela do próprio DTM.')
                  : ListView.builder(
                      itemCount: list.length,
                      itemBuilder: (_, i) => _ParamRow(name: '${list[i]['name']}', group: list[i]['group'] as String?, a: list[i]['value'] as String?, writable: list[i]['writable'] == true),
                    ),
            ),
          ]),
        ),
        actions: [TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Fechar'))],
      );
    }),
  );
}

class _ParamRow extends StatelessWidget {
  const _ParamRow({required this.name, required this.group, required this.a, this.b, this.different = false, this.compare = false, this.writable = false});
  final String name;
  final String? group;
  final String? a;
  final String? b;
  final bool different;
  final bool compare;
  final bool writable;

  @override
  Widget build(BuildContext context) {
    TextStyle v(bool hl) => TextStyle(fontFamily: 'Consolas', fontSize: 12.5, color: hl ? AppTheme.warning : AppTheme.text, fontWeight: hl ? FontWeight.w700 : null);
    String show(String? x) => (x == null || x.isEmpty) ? '—' : x;
    return Container(
      margin: const EdgeInsets.only(bottom: 4),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
      decoration: BoxDecoration(
        color: different ? AppTheme.warning.withAlpha(AppTheme.isLight ? 22 : 30) : AppTheme.cardAlt,
        borderRadius: BorderRadius.circular(10),
        border: different ? Border.all(color: AppTheme.warning.withAlpha(110)) : null,
      ),
      child: Row(children: [
        Expanded(
          flex: 4,
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(name, style: TextStyle(fontWeight: FontWeight.w600, color: AppTheme.text, fontSize: 13)),
            if (group != null && group!.isNotEmpty) Text(group!, style: TextStyle(fontSize: 11, color: AppTheme.textSecondary)),
          ]),
        ),
        Expanded(flex: 3, child: SelectableText(show(a), style: v(false))),
        if (compare) ...[
          Icon(different ? Icons.compare_arrows_rounded : Icons.drag_handle_rounded, size: 16, color: different ? AppTheme.warning : AppTheme.divider),
          const SizedBox(width: 8),
          Expanded(flex: 3, child: SelectableText(show(b), style: v(different))),
        ],
        if (!compare && writable) Tooltip(message: 'Gravável', child: Icon(Icons.edit_rounded, size: 14, color: AppTheme.textSecondary)),
      ]),
    );
  }
}

/// Comparar (nada muda) ou Ler (os valores lidos ficam no DTM, com opção de restaurar o projeto).
Future<void> showCompareDialog(BuildContext context, AppState s, Json node, {required bool keep}) async {
  final tag = node['tag'] as String;
  Json r;
  try {
    r = keep ? await s.upload(tag) : await s.compare(tag);
  } catch (e) {
    s.toast('${keep ? 'Leitura' : 'Comparação'}: $e', error: true);
    return;
  }
  if (!context.mounted) return;
  final items = ((r['items'] as List?) ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList();
  final up = Map<String, dynamic>.from((r['upload'] as Map?) ?? {});
  final refused = ((r['refusedNotes'] as List?) ?? []).cast<String>();
  final messages = ((up['messages'] as List?) ?? []).cast<String>();
  final ok = up['success'] == true;
  var onlyDiff = (r['differences'] as num? ?? 0) > 0;
  final verdict = switch (r['dtmVerdict']) {
    'equal' => 'iguais',
    'notEqual' => 'diferentes',
    'noComparableData' => 'sem dados comparáveis',
    null => 'não informado',
    final v => '$v',
  };
  await showDialog(
    context: context,
    barrierDismissible: false,
    builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
      final list = onlyDiff ? items.where((i) => i['different'] == true).toList() : items;
      return AlertDialog(
        title: Text('${keep ? 'Lido do instrumento' : 'Projeto × instrumento'} · ${node['name']}'),
        content: SizedBox(
          width: 940,
          height: 560,
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            Wrap(spacing: 8, runSpacing: 8, crossAxisAlignment: WrapCrossAlignment.center, children: [
              Pill(ok ? 'leitura concluída' : 'leitura com falha', color: ok ? AppTheme.green : AppTheme.danger, icon: ok ? Icons.check_rounded : Icons.error_outline_rounded),
              Pill('${r['differences']} diferença(s) de ${items.length}', color: (r['differences'] as num? ?? 0) > 0 ? AppTheme.warning : AppTheme.green),
              Pill('veredito do DTM: $verdict', color: AppTheme.accent),
              Pill('${((up['durationMs'] as num? ?? 0) / 1000).toStringAsFixed(1)} s', color: AppTheme.textSecondary),
              FilterChip(label: const Text('Só diferenças'), selected: onlyDiff, onSelected: (v) => set(() => onlyDiff = v)),
            ]),
            if (!ok || refused.isNotEmpty || messages.isNotEmpty) ...[
              const SizedBox(height: 10),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(color: AppTheme.warning.withAlpha(25), borderRadius: BorderRadius.circular(10), border: Border.all(color: AppTheme.warning.withAlpha(100))),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  if (!ok) Text('O DTM informou falha na leitura${up['error'] != null ? ' (${up['error']})' : ''}. Os valores abaixo são os que ele conseguiu ler.',
                      style: TextStyle(color: AppTheme.text, fontSize: 12.5)),
                  for (final m in messages.where((m) => m != up['error'])) Text('DTM: $m', style: TextStyle(color: AppTheme.text, fontSize: 12.5)),
                  if (refused.isNotEmpty)
                    Text('Recusado pela conexão: ${refused.join('; ')}. Para o DTM ler tudo, conecte em modo completo (permite escrita).',
                        style: TextStyle(color: AppTheme.text, fontSize: 12.5)),
                ]),
              ),
            ],
            const SizedBox(height: 10),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: Row(children: [
                Expanded(flex: 4, child: Text('Parâmetro', style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.textSecondary, fontSize: 12))),
                Expanded(flex: 3, child: Text('Projeto (DTM)', style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.textSecondary, fontSize: 12))),
                const SizedBox(width: 24),
                Expanded(flex: 3, child: Text('Instrumento', style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.textSecondary, fontSize: 12))),
              ]),
            ),
            const SizedBox(height: 6),
            Expanded(
              child: list.isEmpty
                  ? EmptyState(icon: Icons.check_circle_outline_rounded, title: onlyDiff ? 'Nenhuma diferença' : 'O DTM não publicou parâmetros')
                  : ListView.builder(
                      itemCount: list.length,
                      itemBuilder: (_, i) => _ParamRow(
                        name: '${list[i]['name']}',
                        group: list[i]['group'] as String?,
                        a: list[i]['projectValue'] as String?,
                        b: list[i]['deviceValue'] as String?,
                        different: list[i]['different'] == true,
                        compare: true,
                      ),
                    ),
            ),
            const SizedBox(height: 6),
            Text(
              keep
                  ? (r['keptDeviceValues'] == true
                      ? 'Os valores lidos ficaram no DTM (salve o projeto para guardá-los). Você pode restaurar os valores do projeto.'
                      : 'Como a leitura falhou, o projeto foi mantido como estava.')
                  : 'Comparação apenas: o projeto foi restaurado e nada foi alterado.',
              style: TextStyle(color: AppTheme.textSecondary, fontSize: 12),
            ),
          ]),
        ),
        actions: [
          if (r['canRevert'] == true)
            OutlinedButton.icon(
              onPressed: () async {
                Navigator.pop(ctx);
                await s.revert(tag);
              },
              icon: const Icon(Icons.undo_rounded, size: 18),
              label: const Text('Restaurar o projeto'),
            ),
          ElevatedButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(r['canRevert'] == true ? 'Manter valores lidos' : 'Fechar'),
          ),
        ],
      );
    }),
  );
}

/// Gravar no instrumento: confirmação explícita (o Host exige política "completo" e confirmação, e audita).
Future<void> confirmDownload(BuildContext context, AppState s, Json node) async {
  var checked = false;
  final go = await showDialog<bool>(
    context: context,
    builder: (ctx) => StatefulBuilder(
      builder: (ctx, set) => AlertDialog(
        title: Row(children: [
          Icon(Icons.warning_amber_rounded, color: AppTheme.danger),
          const SizedBox(width: 10),
          Expanded(child: Text('Gravar no instrumento ${node['name']}?')),
        ]),
        content: SizedBox(
          width: 500,
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(
              'Todos os parâmetros do projeto serão escritos no instrumento pelo DTM. Isso pode mudar o comportamento da malha (faixa, unidade, amortecimento, modo de saída).',
              style: TextStyle(color: AppTheme.text, height: 1.4),
            ),
            const SizedBox(height: 10),
            Text('Dica: use antes "Comparar com o instrumento" para ver o que vai mudar.', style: TextStyle(color: AppTheme.textSecondary, fontSize: 12.5)),
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              value: checked,
              onChanged: (v) => set(() => checked = v ?? false),
              title: Text('A malha está em manual/segura e estou autorizado a alterar este instrumento.', style: TextStyle(fontSize: 13, color: AppTheme.text)),
              subtitle: Text('Fica registrado na auditoria da planta.', style: TextStyle(fontSize: 12, color: AppTheme.textSecondary)),
            ),
          ]),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancelar')),
          ElevatedButton(
            style: ElevatedButton.styleFrom(backgroundColor: AppTheme.danger),
            onPressed: checked ? () => Navigator.pop(ctx, true) : null,
            child: const Text('Gravar no instrumento'),
          ),
        ],
      ),
    ),
  );
  if (go != true) return;
  try {
    final r = await s.download(node['tag'] as String);
    final msgs = ((r['messages'] as List?) ?? []).cast<String>();
    if (r['success'] == true) {
      s.toast('${node['name']}: parâmetros gravados no instrumento.');
    } else {
      s.toast('${node['name']}: o DTM informou falha na gravação${r['error'] != null ? ' — ${r['error']}' : ''}${msgs.isNotEmpty ? ' (${msgs.join('; ')})' : ''}.', error: true);
    }
  } on HostError catch (e) {
    s.toast(e.message, error: true);
  }
}
