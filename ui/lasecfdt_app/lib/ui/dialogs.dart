import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import 'widgets.dart';

Future<String?> askText(BuildContext context, {required String title, String label = 'Nome', String initial = '', String action = 'OK'}) {
  final ctrl = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: SizedBox(
        width: 420,
        child: TextField(
          controller: ctrl,
          autofocus: true,
          decoration: InputDecoration(labelText: label),
          onSubmitted: (v) => Navigator.pop(ctx, v.trim()),
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancelar')),
        ElevatedButton(onPressed: () => Navigator.pop(ctx, ctrl.text.trim()), child: Text(action)),
      ],
    ),
  );
}

Future<bool> confirm(BuildContext context, {required String title, required String message, String action = 'Confirmar', bool danger = false}) async {
  final r = await showDialog<bool>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: SizedBox(width: 440, child: Text(message, style: TextStyle(color: AppTheme.textSecondary, height: 1.4))),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancelar')),
        ElevatedButton(
          style: danger ? ElevatedButton.styleFrom(backgroundColor: AppTheme.danger) : null,
          onPressed: () => Navigator.pop(ctx, true),
          child: Text(action),
        ),
      ],
    ),
  );
  return r == true;
}

/// Política de comunicação da conexão. "Completo" (escrita no instrumento) exige confirmação explícita, que o
/// Host também exige e registra na auditoria da planta.
Future<(String, bool)?> askConnection(BuildContext context, String nodeName) {
  var policy = 'ReadOnly';
  var confirmed = false;
  return showDialog<(String, bool)>(
    context: context,
    builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
      Widget option(String value, IconData icon, String title, String text, Color color) => Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: InkWell(
              borderRadius: BorderRadius.circular(12),
              onTap: () => set(() => policy = value),
              child: Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(12),
                  border: Border.all(color: policy == value ? color : AppTheme.divider, width: policy == value ? 2 : 1),
                  color: policy == value ? color.withAlpha(25) : null,
                ),
                child: Row(children: [
                  Icon(icon, color: color),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text(title, style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.text)),
                      Text(text, style: TextStyle(fontSize: 12.5, color: AppTheme.textSecondary)),
                    ]),
                  ),
                  if (policy == value) Icon(Icons.check_circle_rounded, color: color),
                ]),
              ),
            ),
          );
      final ok = policy != 'Full' || confirmed;
      return AlertDialog(
        title: Text('Conectar $nodeName'),
        content: SizedBox(
          width: 480,
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            option('ReadOnly', Icons.visibility_rounded, 'Somente leitura (recomendado)',
                'Só passam comandos HART de leitura conhecidos. Escritas e comandos do fabricante são recusados.', AppTheme.green),
            option('Full', Icons.edit_note_rounded, 'Completo — permite escrita',
                'Tudo passa, inclusive escrita e download de parâmetros para o instrumento.', AppTheme.danger),
            option('Blocked', Icons.block_rounded, 'Bloqueado', 'O DTM fica ligado à topologia, mas nada chega ao instrumento.', AppTheme.textSecondary),
            if (policy == 'Full')
              CheckboxListTile(
                contentPadding: EdgeInsets.zero,
                value: confirmed,
                onChanged: (v) => set(() => confirmed = v ?? false),
                title: Text('Confirmo que estou autorizado a escrever neste instrumento e que o processo está em condição segura.',
                    style: TextStyle(fontSize: 13, color: AppTheme.text)),
                subtitle: Text('A confirmação fica registrada na auditoria da planta com a estação e o usuário.',
                    style: TextStyle(fontSize: 12, color: AppTheme.textSecondary)),
              ),
          ]),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancelar')),
          ElevatedButton(onPressed: ok ? () => Navigator.pop(ctx, (policy, confirmed)) : null, child: const Text('Conectar')),
        ],
      );
    }),
  );
}

/// Seletor de DTM do catálogo para adicionar à topologia (abaixo de [parentName] ou na raiz).
Future<(Map<String, dynamic>, int, String)?> pickDtm(
  BuildContext context, {
  required List<Map<String, dynamic>> catalog,
  required String? parentName,
  required Future<Map<String, dynamic>?> Function(String clsid) describe,
}) {
  var query = '';
  Map<String, dynamic>? chosen;
  var typeIndex = 0;
  var loadingTypes = false;
  final nameCtrl = TextEditingController();
  final underParent = parentName != null;
  return showDialog(
    context: context,
    builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
      final list = catalog.where((e) {
        if (e['hostable'] != true) return false;
        if (underParent && e['role'] == 'Communication') return false;
        final q = query.toLowerCase();
        return q.isEmpty || '${e['name']} ${e['vendor']} ${e['progId']}'.toLowerCase().contains(q);
      }).toList()
        ..sort((a, b) {
          int rank(Map e) => underParent ? (e['role'] == 'Device' ? 0 : 1) : (e['role'] == 'Device' ? 1 : 0);
          final r = rank(a).compareTo(rank(b));
          return r != 0 ? r : '${a['vendor']} ${a['name']}'.compareTo('${b['vendor']} ${b['name']}');
        });
      final types = (chosen?['deviceTypes'] as List?)?.cast<Map>() ?? const [];

      Future<void> choose(Map<String, dynamic> e) async {
        set(() {
          chosen = e;
          typeIndex = 0;
          loadingTypes = e['deviceTypes'] == null;
        });
        if (e['deviceTypes'] == null) {
          final d = await describe(e['clsid'] as String);
          if (!ctx.mounted) return;
          set(() {
            if (d != null) chosen = {...e, 'deviceTypes': d['deviceTypes'], 'name': d['name'] ?? e['name'], 'vendor': d['vendor'] ?? e['vendor']};
            loadingTypes = false;
          });
        }
      }

      return AlertDialog(
        title: Text(underParent ? 'Adicionar dispositivo em $parentName' : 'Adicionar ao PC (comunicação ou gateway)'),
        content: SizedBox(
          width: 820,
          height: 520,
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Expanded(
              flex: 5,
              child: Column(children: [
                TextField(
                  autofocus: true,
                  decoration: const InputDecoration(prefixIcon: Icon(Icons.search_rounded), hintText: 'Buscar por nome, fabricante…'),
                  onChanged: (v) => set(() => query = v),
                ),
                const SizedBox(height: 10),
                Expanded(
                  child: ListView.builder(
                    itemCount: list.length,
                    itemBuilder: (_, i) {
                      final e = list[i];
                      final sel = chosen?['clsid'] == e['clsid'];
                      return ListTile(
                        dense: true,
                        selected: sel,
                        selectedTileColor: AppTheme.primary.withAlpha(30),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
                        leading: Icon(roleIcon(e['role'] as String?), color: roleColor(e['role'] as String?)),
                        title: Text('${e['name']}', style: TextStyle(color: AppTheme.text, fontWeight: FontWeight.w600)),
                        subtitle: Text('${e['vendor'] ?? '—'} · ${roleLabel(e['role'] as String?)} · v${e['version'] ?? '?'}',
                            style: TextStyle(color: AppTheme.textSecondary, fontSize: 12)),
                        onTap: () => choose(e),
                      );
                    },
                  ),
                ),
              ]),
            ),
            const SizedBox(width: 16),
            Expanded(
              flex: 4,
              child: chosen == null
                  ? Center(child: Text('Escolha um DTM na lista.', style: TextStyle(color: AppTheme.textSecondary)))
                  : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                      Text('${chosen!['name']}', style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: AppTheme.text)),
                      Text('${chosen!['vendor'] ?? ''}', style: TextStyle(color: AppTheme.textSecondary)),
                      const SizedBox(height: 14),
                      Text('Tipo de dispositivo', style: TextStyle(fontWeight: FontWeight.w600, color: AppTheme.text)),
                      const SizedBox(height: 6),
                      if (loadingTypes)
                        Row(children: [
                          busySpinner(),
                          const SizedBox(width: 10),
                          Expanded(
                              child: Text('Lendo o DTM (só na primeira vez neste servidor)…', style: TextStyle(color: AppTheme.textSecondary))),
                        ])
                      else if (types.isEmpty)
                        Text('Tipo padrão do DTM.', style: TextStyle(color: AppTheme.textSecondary))
                      else
                        DropdownButtonFormField<int>(
                          initialValue: typeIndex,
                          isExpanded: true,
                          items: [
                            for (final t in types)
                              DropdownMenuItem(
                                value: (t['index'] as num).toInt(),
                                child: Text('${t['name']}${t['version'] != null ? '  (v${t['version']})' : ''}', overflow: TextOverflow.ellipsis),
                              ),
                          ],
                          onChanged: (v) => set(() => typeIndex = v ?? 0),
                        ),
                      const SizedBox(height: 14),
                      TextField(controller: nameCtrl, decoration: const InputDecoration(labelText: 'Nome na topologia (opcional)', hintText: 'ex.: PT-101')),
                      const Spacer(),
                      Text(
                        underParent
                            ? 'O DTM é criado num processo próprio e ligado ao canal do nó pai. Nenhuma comunicação é feita até você conectar.'
                            : 'Depois de adicionar, abra a Configuração do DTM para escolher a porta, o modem ou o gateway.',
                        style: TextStyle(fontSize: 12, color: AppTheme.textSecondary, height: 1.4),
                      ),
                    ]),
            ),
          ]),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancelar')),
          ElevatedButton(
            onPressed: chosen == null || loadingTypes ? null : () => Navigator.pop(ctx, (chosen!, typeIndex, nameCtrl.text.trim())),
            child: const Text('Adicionar'),
          ),
        ],
      );
    }),
  );
}
