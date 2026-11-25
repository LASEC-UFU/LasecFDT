import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../core/native_dock.dart';
import '../state/app_state.dart';
import 'dialogs.dart';
import 'parameters_dialog.dart';
import 'scan_dialog.dart';
import 'widgets.dart';

class ProjectPage extends StatelessWidget {
  const ProjectPage({required this.state, super.key});
  final AppState state;

  @override
  Widget build(BuildContext context) => state.project == null ? _StartView(state: state) : _ProjectView(state: state);
}

// ==================================================================== início (sem projeto)

class _StartView extends StatefulWidget {
  const _StartView({required this.state});
  final AppState state;

  @override
  State<_StartView> createState() => _StartViewState();
}

class _StartViewState extends State<_StartView> {
  Future<List<Json>>? _projects;

  @override
  void initState() {
    super.initState();
    _projects = widget.state.listProjects();
  }

  void _reload() => setState(() => _projects = widget.state.listProjects());

  Future<void> _new() async {
    final name = await askText(context, title: 'Novo projeto', label: 'Nome do projeto', action: 'Criar');
    if (name == null || name.isEmpty) return;
    if (await widget.state.newProject(name)) _reload();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.state;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 6, 20, 20),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        SizedBox(
          width: 340,
          child: Column(children: [
            _ActionCard(
              icon: Icons.add_circle_outline_rounded,
              color: AppTheme.primary,
              title: 'Novo projeto',
              text: 'Monte a topologia: PC → comunicação (modem, HART-IP) → instrumentos.',
              onTap: _new,
            ),
            const SizedBox(height: 12),
            _ActionCard(
              icon: Icons.inventory_2_rounded,
              color: AppTheme.accent,
              title: 'Catálogo de DTMs',
              text: '${s.catalog.length} DTMs instalados no servidor, compartilhados por todas as estações.',
              onTap: () => s.go(AppPage.catalog),
            ),
          ]),
        ),
        const SizedBox(width: 20),
        Expanded(
          child: AppCard(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              SectionTitle('Projetos da planta',
                  trailing: IconButton(tooltip: 'Atualizar', onPressed: _reload, icon: Icon(Icons.refresh_rounded, color: AppTheme.textSecondary))),
              Expanded(
                child: FutureBuilder<List<Json>>(
                  future: _projects,
                  builder: (context, snap) {
                    if (!snap.hasData) return Center(child: busySpinner(size: 24));
                    final list = snap.data!;
                    if (list.isEmpty) {
                      return EmptyState(
                        icon: Icons.folder_open_rounded,
                        title: 'Nenhum projeto ainda',
                        message: 'Os projetos ficam na pasta comum da planta e aparecem para todas as estações.',
                        action: ElevatedButton.icon(onPressed: _new, icon: const Icon(Icons.add_rounded), label: const Text('Novo projeto')),
                      );
                    }
                    return ListView.separated(
                      itemCount: list.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 8),
                      itemBuilder: (_, i) {
                        final p = list[i];
                        final locked = p['lockedBy'] != null && p['lockedBy'] != s.station['id'];
                        return Material(
                          color: AppTheme.cardAlt,
                          borderRadius: BorderRadius.circular(12),
                          child: ListTile(
                            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
                            leading: Icon(Icons.account_tree_rounded, color: AppTheme.primary),
                            title: Text('${p['name']}', style: TextStyle(fontWeight: FontWeight.w600, color: AppTheme.text)),
                            subtitle: Text('${p['nodes']} nó(s) · alterado em ${_date(p['modifiedUtc'])}',
                                style: TextStyle(color: AppTheme.textSecondary, fontSize: 12)),
                            trailing: locked
                                ? Pill('Em edição em ${p['lockedBy']}', color: AppTheme.warning, icon: Icons.lock_rounded)
                                : Icon(Icons.chevron_right_rounded, color: AppTheme.textSecondary),
                            onTap: () => s.openProject(p['name'] as String),
                          ),
                        );
                      },
                    );
                  },
                ),
              ),
            ]),
          ),
        ),
      ]),
    );
  }

  static String _date(dynamic iso) {
    final d = DateTime.tryParse('$iso')?.toLocal();
    if (d == null) return '?';
    String two(int v) => v.toString().padLeft(2, '0');
    return '${two(d.day)}/${two(d.month)}/${d.year} ${two(d.hour)}:${two(d.minute)}';
  }
}

class _ActionCard extends StatelessWidget {
  const _ActionCard({required this.icon, required this.color, required this.title, required this.text, required this.onTap});
  final IconData icon;
  final Color color;
  final String title;
  final String text;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => InkWell(
        borderRadius: BorderRadius.circular(16),
        onTap: onTap,
        child: AppCard(
          child: Row(children: [
            IconBadge(icon, color: color, size: 26),
            const SizedBox(width: 14),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(title, style: TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: AppTheme.text)),
                const SizedBox(height: 4),
                Text(text, style: TextStyle(fontSize: 12.5, color: AppTheme.textSecondary, height: 1.35)),
              ]),
            ),
          ]),
        ),
      );
}

// ==================================================================== projeto aberto

class _ProjectView extends StatelessWidget {
  const _ProjectView({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 4, 20, 20),
      child: Column(children: [
        _Toolbar(state: state),
        const SizedBox(height: 12),
        Expanded(
          child: Row(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            SizedBox(width: 330, child: _TopologyPanel(state: state)),
            const SizedBox(width: 14),
            Expanded(child: _WorkArea(state: state)),
          ]),
        ),
      ]),
    );
  }
}

/// Botão "Parar": interrompe na hora o que está em andamento ou na fila nesta estação (AppState.stopAll).
class StopButton extends StatelessWidget {
  const StopButton({required this.state, this.label = 'Parar', super.key});
  final AppState state;
  final String label;

  @override
  Widget build(BuildContext context) => ElevatedButton.icon(
        style: ElevatedButton.styleFrom(backgroundColor: AppTheme.danger, foregroundColor: Colors.white),
        onPressed: state.stopping ? null : state.stopAll,
        icon: state.stopping ? busySpinner(color: Colors.white) : const Icon(Icons.stop_rounded, size: 18),
        label: Text(state.stopping ? 'Parando…' : label),
      );
}

class _Toolbar extends StatelessWidget {
  const _Toolbar({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    final ro = state.projectReadOnly;
    return Row(children: [
      ElevatedButton.icon(
        onPressed: ro ? null : () => state.saveProject(),
        icon: const Icon(Icons.save_rounded, size: 18),
        label: const Text('Salvar'),
      ),
      const SizedBox(width: 8),
      OutlinedButton.icon(
        onPressed: () async {
          final name = await askText(context, title: 'Salvar como', label: 'Novo nome do projeto', action: 'Salvar');
          if (name != null && name.isNotEmpty) await state.saveProject(saveAs: name);
        },
        icon: const Icon(Icons.save_as_rounded, size: 18),
        label: const Text('Salvar como'),
      ),
      const SizedBox(width: 8),
      OutlinedButton.icon(
        onPressed: () async {
          if (state.projectDirty &&
              !await confirm(context,
                  title: 'Fechar projeto',
                  message: 'Há alterações não salvas. Fechar mesmo assim? Os DTMs serão liberados e as telas fechadas.',
                  action: 'Fechar sem salvar',
                  danger: true)) {
            return;
          }
          await state.closeProject();
        },
        icon: const Icon(Icons.close_rounded, size: 18),
        label: const Text('Fechar'),
      ),
      if (state.working) ...[
        const SizedBox(width: 16),
        StopButton(state: state),
        const SizedBox(width: 10),
        Flexible(
          child: Text(state.busy.values.join(' · '), overflow: TextOverflow.ellipsis, style: TextStyle(color: AppTheme.textSecondary, fontSize: 12.5)),
        ),
      ],
      const Spacer(),
      if (ro)
        Text('Somente leitura: use "Salvar como" para criar uma cópia sua.', style: TextStyle(color: AppTheme.warning, fontSize: 12.5)),
    ]);
  }
}

// -------------------------------------------------------------------- árvore

class _TopologyPanel extends StatelessWidget {
  const _TopologyPanel({required this.state});
  final AppState state;

  Future<void> _addUnder(BuildContext context, String? parentTag) async {
    final parent = parentTag == null ? null : state.nodes[parentTag];
    if (state.catalog.isEmpty) await state.loadCatalog();
    if (!context.mounted) return;
    final r = await pickDtm(context, catalog: state.catalog, parentName: parent?['name'] as String?, describe: state.describe);
    if (r == null) return;
    await state.addNode(clsid: r.$1['clsid'] as String, deviceTypeIndex: r.$2, parentTag: parentTag, name: r.$3);
  }

  @override
  Widget build(BuildContext context) {
    final roots = state.roots;
    return AppCard(
      padding: const EdgeInsets.fromLTRB(10, 12, 10, 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 6),
          child: SectionTitle('Topologia',
              trailing: state.busy.containsKey('_root')
                  ? busySpinner()
                  : IconButton(
                      tooltip: 'Adicionar comunicação/gateway ao PC',
                      onPressed: state.projectReadOnly ? null : () => _addUnder(context, null),
                      icon: Icon(Icons.add_rounded, color: AppTheme.primary),
                    )),
        ),
        _TreeRow(
          icon: Icons.computer_rounded,
          color: AppTheme.textSecondary,
          title: 'PC · ${state.station['id']}',
          subtitle: 'Estação',
          depth: 0,
          selected: false,
          onTap: () => state.select(null),
        ),
        Expanded(
          child: roots.isEmpty
              ? Padding(
                  padding: const EdgeInsets.all(12),
                  child: Text('Adicione um DTM de comunicação (modem HART, HART-IP) com o botão +.',
                      style: TextStyle(color: AppTheme.textSecondary, fontSize: 12.5, height: 1.4)),
                )
              : ListView(children: [for (final r in roots) ..._branch(context, r, 1)]),
        ),
      ]),
    );
  }

  List<Widget> _branch(BuildContext context, Json n, int depth) {
    final tag = n['tag'] as String;
    final busy = state.busy[tag];
    return [
      _TreeRow(
        icon: roleIcon(n['role'] as String?),
        color: roleColor(n['role'] as String?),
        title: '${n['name']}',
        subtitle: busy ?? '${n['deviceType'] ?? n['dtmName'] ?? ''}',
        state: n['state'] as String?,
        busy: busy != null,
        depth: depth,
        selected: state.selectedTag == tag,
        uiOpen: n['uiOpen'] == true,
        onTap: () {
          state.select(tag);
          final i = state.tabs.indexWhere((t) => t.tag == tag);
          if (i >= 0) state.setActiveTab(i);
        },
        onDoubleTap: () => state.openUi(tag),
        onSecondaryTap: (pos) => _NodeMenu.show(context, state, n, pos, onAdd: () => _addUnder(context, tag)),
      ),
      for (final c in state.childrenOf(tag)) ..._branch(context, c, depth + 1),
    ];
  }
}

class _TreeRow extends StatelessWidget {
  const _TreeRow({
    required this.icon,
    required this.color,
    required this.title,
    required this.subtitle,
    required this.depth,
    required this.selected,
    required this.onTap,
    this.state,
    this.busy = false,
    this.uiOpen = false,
    this.onDoubleTap,
    this.onSecondaryTap,
  });
  final IconData icon;
  final Color color;
  final String title;
  final String subtitle;
  final int depth;
  final bool selected;
  final String? state;
  final bool busy;
  final bool uiOpen;
  final VoidCallback onTap;
  final VoidCallback? onDoubleTap;
  final void Function(Offset)? onSecondaryTap;

  @override
  Widget build(BuildContext context) => GestureDetector(
        onSecondaryTapDown: onSecondaryTap == null ? null : (d) => onSecondaryTap!(d.globalPosition),
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: onTap,
          onDoubleTap: onDoubleTap,
          child: Container(
            margin: const EdgeInsets.symmetric(vertical: 1),
            padding: EdgeInsets.fromLTRB(8.0 + depth * 18, 7, 8, 7),
            decoration: BoxDecoration(
              color: selected ? AppTheme.primary.withAlpha(AppTheme.isLight ? 30 : 45) : null,
              borderRadius: BorderRadius.circular(10),
            ),
            child: Row(children: [
              if (depth > 0) ...[Icon(Icons.subdirectory_arrow_right_rounded, size: 14, color: AppTheme.divider), const SizedBox(width: 4)],
              Icon(icon, size: 20, color: color),
              const SizedBox(width: 10),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Text(title, overflow: TextOverflow.ellipsis, style: TextStyle(fontWeight: FontWeight.w600, color: AppTheme.text, fontSize: 13.5)),
                  if (subtitle.isNotEmpty)
                    Text(subtitle, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, color: AppTheme.textSecondary)),
                ]),
              ),
              if (uiOpen) Padding(padding: const EdgeInsets.only(right: 6), child: Icon(Icons.web_asset_rounded, size: 15, color: AppTheme.textSecondary)),
              if (busy) busySpinner(size: 13) else if (state != null) Tooltip(message: stateLabel(state), child: StateDot(state)),
            ]),
          ),
        ),
      );
}

/// Menu de contexto do nó (também usado pelo painel de detalhes).
class _NodeMenu {
  static Future<void> show(BuildContext context, AppState s, Json n, Offset pos, {required VoidCallback onAdd}) async {
    final online = n['state'] == 'online';
    final ro = s.projectReadOnly;
    final hasParent = n['parentTag'] != null;
    final v = await showMenu<String>(
      context: context,
      position: RelativeRect.fromLTRB(pos.dx, pos.dy, pos.dx + 1, pos.dy + 1),
      items: [
        const PopupMenuItem(value: 'open', child: ListTile(dense: true, leading: Icon(Icons.open_in_new_rounded), title: Text('Abrir tela padrão'))),
        const PopupMenuItem(value: 'functions', child: ListTile(dense: true, leading: Icon(Icons.apps_rounded), title: Text('Funções do DTM…'))),
        if (hasParent && !online)
          const PopupMenuItem(value: 'connect', child: ListTile(dense: true, leading: Icon(Icons.link_rounded), title: Text('Conectar…'))),
        if (online) const PopupMenuItem(value: 'disconnect', child: ListTile(dense: true, leading: Icon(Icons.link_off_rounded), title: Text('Desconectar'))),
        if (n['role'] == 'Device') const PopupMenuDivider(),
        if (n['role'] == 'Device') const PopupMenuItem(value: 'params', child: ListTile(dense: true, leading: Icon(Icons.tune_rounded), title: Text('Parâmetros…'))),
        if (online && n['role'] == 'Device')
          const PopupMenuItem(value: 'compare', child: ListTile(dense: true, leading: Icon(Icons.compare_arrows_rounded), title: Text('Comparar com o instrumento'))),
        if (online && n['role'] == 'Device' && !ro)
          const PopupMenuItem(value: 'upload', child: ListTile(dense: true, leading: Icon(Icons.download_rounded), title: Text('Ler do instrumento'))),
        if (online && n['role'] == 'Device' && !ro)
          const PopupMenuItem(value: 'download', child: ListTile(dense: true, leading: Icon(Icons.upload_rounded), title: Text('Gravar no instrumento…'))),
        if (!ro && n['role'] != 'Device')
          const PopupMenuItem(value: 'scan', child: ListTile(dense: true, leading: Icon(Icons.radar_rounded), title: Text('Buscar instrumentos…'))),
        if (!ro && n['role'] != 'Device')
          const PopupMenuItem(value: 'add', child: ListTile(dense: true, leading: Icon(Icons.add_rounded), title: Text('Adicionar dispositivo…'))),
        const PopupMenuDivider(),
        if (!ro) const PopupMenuItem(value: 'rename', child: ListTile(dense: true, leading: Icon(Icons.edit_rounded), title: Text('Renomear'))),
        if (n['workerPid'] != null && !online)
          const PopupMenuItem(value: 'unload', child: ListTile(dense: true, leading: Icon(Icons.power_settings_new_rounded), title: Text('Descarregar DTM'))),
        if (!ro) const PopupMenuItem(value: 'remove', child: ListTile(dense: true, leading: Icon(Icons.delete_outline_rounded), title: Text('Remover'))),
      ],
    );
    if (!context.mounted || v == null) return;
    await NodeActions.run(context, s, n, v, onAdd: onAdd);
  }
}

class NodeActions {
  static Future<void> run(BuildContext context, AppState s, Json n, String action, {VoidCallback? onAdd}) async {
    final tag = n['tag'] as String;
    switch (action) {
      case 'open':
        await s.openUi(tag);
      case 'functions':
        await showFunctions(context, s, n);
      case 'connect':
        final r = await askConnection(context, '${n['name']}');
        if (r != null) await s.connect(tag, policy: r.$1, confirmFull: r.$2);
      case 'disconnect':
        await s.disconnect(tag);
      case 'add':
        onAdd?.call();
      case 'scan':
        await showScanDialog(context, s, n);
      case 'params':
        await showParametersDialog(context, s, n);
      case 'compare':
        await showCompareDialog(context, s, n, keep: false);
      case 'upload':
        await showCompareDialog(context, s, n, keep: true);
      case 'download':
        if (n['policy'] != 'Full') {
          s.toast('Para gravar, desconecte e conecte em modo completo (permite escrita).', error: true);
        } else {
          await confirmDownload(context, s, n);
        }
      case 'rename':
        final name = await askText(context, title: 'Renomear', initial: '${n['name']}', action: 'Renomear');
        if (name != null && name.isNotEmpty) await s.renameNode(tag, name);
      case 'unload':
        await s.unload(tag);
      case 'remove':
        final kids = s.childrenOf(tag).length;
        if (context.mounted &&
            await confirm(context,
                title: 'Remover ${n['name']}',
                message: kids > 0
                    ? 'O nó e os $kids dispositivo(s) abaixo dele serão removidos do projeto (os DTMs são liberados pelo protocolo).'
                    : 'O nó será removido do projeto (o DTM é liberado pelo protocolo).',
                action: 'Remover',
                danger: true)) {
          await s.removeNode(tag);
        }
    }
  }

  /// Lista as funções do DTM e dos canais dele (ex.: configuração da porta de um Comm DTM).
  static Future<void> showFunctions(BuildContext context, AppState s, Json n) async {
    final tag = n['tag'] as String;
    final f = await s.functions(tag);
    if (f == null || !context.mounted) return;
    final funcs = ((f['functions'] as List?) ?? []).cast<Map>().where((x) => x['hidden'] != true).toList();
    final chans = ((f['channels'] as List?) ?? []).cast<Map>().where((c) => ((c['functions'] as List?) ?? []).isNotEmpty).toList();
    await showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text('Funções · ${n['name']}'),
        content: SizedBox(
          width: 480,
          child: ListView(shrinkWrap: true, children: [
            if (funcs.isEmpty && chans.isEmpty) Text('O DTM não publicou funções.', style: TextStyle(color: AppTheme.textSecondary)),
            for (final x in funcs)
              ListTile(
                enabled: x['enabled'] != false,
                leading: Icon(Icons.web_rounded, color: AppTheme.primary),
                title: Text('${x['name']}'),
                subtitle: Text('${x['applicationId'] ?? 'função ${x['functionId']}'}', style: TextStyle(color: AppTheme.textSecondary, fontSize: 12)),
                onTap: () {
                  Navigator.pop(ctx);
                  s.openUi(tag, functionId: '${x['functionId']}', label: '${x['name']}');
                },
              ),
            for (final c in chans) ...[
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
                child: Text('Canal ${c['path']}', style: TextStyle(fontWeight: FontWeight.w700, color: AppTheme.textSecondary, fontSize: 12)),
              ),
              for (final x in ((c['functions'] as List).cast<Map>()).where((x) => x['hidden'] != true))
                ListTile(
                  leading: Icon(Icons.settings_input_component_rounded, color: AppTheme.accent),
                  title: Text('${x['name']}'),
                  onTap: () {
                    Navigator.pop(ctx);
                    s.openUi(tag, functionId: '${x['functionId']}', channelPath: '${c['path']}', label: '${x['name']}');
                  },
                ),
            ],
          ]),
        ),
        actions: [TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Fechar'))],
      ),
    );
  }
}

// -------------------------------------------------------------------- área de trabalho (abas + detalhes)

class _WorkArea extends StatelessWidget {
  const _WorkArea({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    final active = state.activeTab >= 0 && state.activeTab < state.tabs.length ? state.tabs[state.activeTab] : null;
    return AppCard(
      padding: EdgeInsets.zero,
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        _TabStrip(state: state),
        Divider(height: 1, color: AppTheme.divider),
        Expanded(
          child: active == null
              ? _NodeDetails(state: state)
              : ClipRRect(
                  borderRadius: const BorderRadius.vertical(bottom: Radius.circular(16)),
                  child: Container(
                    color: Colors.white, // fundo enquanto a janela do DTM se posiciona
                    child: DockSlot(hwnd: active.hwnd, visible: state.page == AppPage.project),
                  ),
                ),
        ),
      ]),
    );
  }
}

class _TabStrip extends StatelessWidget {
  const _TabStrip({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    Widget tab({required String title, required bool sel, required VoidCallback onTap, VoidCallback? onClose, IconData icon = Icons.web_asset_rounded}) =>
        InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(10),
          child: Container(
            margin: const EdgeInsets.only(right: 6),
            padding: const EdgeInsets.fromLTRB(12, 8, 6, 8),
            decoration: BoxDecoration(
              color: sel ? AppTheme.primary.withAlpha(AppTheme.isLight ? 30 : 50) : AppTheme.cardAlt,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: sel ? AppTheme.primary.withAlpha(140) : AppTheme.divider),
            ),
            child: Row(mainAxisSize: MainAxisSize.min, children: [
              Icon(icon, size: 16, color: sel ? AppTheme.primary : AppTheme.textSecondary),
              const SizedBox(width: 8),
              ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 260),
                child: Text(title, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: AppTheme.text)),
              ),
              if (onClose != null)
                InkWell(
                  borderRadius: BorderRadius.circular(6),
                  onTap: onClose,
                  child: Padding(padding: const EdgeInsets.all(3), child: Icon(Icons.close_rounded, size: 15, color: AppTheme.textSecondary)),
                )
              else
                const SizedBox(width: 6),
            ]),
          ),
        );

    return SizedBox(
      height: 52,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.fromLTRB(10, 8, 10, 6),
        children: [
          tab(title: 'Detalhes', sel: state.activeTab < 0, onTap: state.showDetails, icon: Icons.info_outline_rounded),
          for (var i = 0; i < state.tabs.length; i++)
            tab(
              title: state.tabs[i].title,
              sel: state.activeTab == i,
              onTap: () => state.setActiveTab(i),
              onClose: () => state.closeTab(state.tabs[i]),
            ),
        ],
      ),
    );
  }
}

class _NodeDetails extends StatelessWidget {
  const _NodeDetails({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    final n = state.selected;
    if (n == null) {
      return EmptyState(
        icon: Icons.touch_app_rounded,
        title: 'Selecione um nó da topologia',
        message: 'Duplo clique abre a tela padrão do DTM. Botão direito mostra todas as ações.',
      );
    }
    final tag = n['tag'] as String;
    final online = n['state'] == 'online';
    final busy = state.busy[tag];
    final hasParent = n['parentTag'] != null;
    final ro = state.projectReadOnly;
    final parent = hasParent ? state.nodes[n['parentTag']] : null;
    Future<void> act(String a) => NodeActions.run(context, state, n, a, onAdd: () async {
          if (state.catalog.isEmpty) await state.loadCatalog();
          if (!context.mounted) return;
          final r = await pickDtm(context, catalog: state.catalog, parentName: n['name'] as String?, describe: state.describe);
          if (r != null) await state.addNode(clsid: r.$1['clsid'] as String, deviceTypeIndex: r.$2, parentTag: tag, name: r.$3);
        });

    return SingleChildScrollView(
      padding: const EdgeInsets.all(22),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          IconBadge(roleIcon(n['role'] as String?), color: roleColor(n['role'] as String?), size: 28),
          const SizedBox(width: 14),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('${n['name']}', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700, color: AppTheme.text)),
              Text('${n['vendor'] ?? ''} · ${n['dtmName'] ?? ''}', style: TextStyle(color: AppTheme.textSecondary)),
            ]),
          ),
          if (busy != null) ...[
            busySpinner(),
            const SizedBox(width: 8),
            Text(busy, style: TextStyle(color: AppTheme.textSecondary)),
            const SizedBox(width: 10),
            StopButton(state: state),
          ],
          const SizedBox(width: 12),
          Pill(stateLabel(n['state'] as String?), color: stateColor(n['state'] as String?), icon: Icons.circle),
          if (online && n['policy'] != null) ...[
            const SizedBox(width: 8),
            Pill(AppState.policyLabel(n['policy'] as String?), color: n['policy'] == 'Full' ? AppTheme.danger : AppTheme.green, icon: Icons.shield_rounded),
          ],
        ]),
        const SizedBox(height: 18),
        Wrap(spacing: 8, runSpacing: 8, children: [
          ElevatedButton.icon(onPressed: busy != null ? null : () => act('open'), icon: const Icon(Icons.open_in_new_rounded, size: 18), label: const Text('Abrir tela')),
          OutlinedButton.icon(onPressed: busy != null ? null : () => act('functions'), icon: const Icon(Icons.apps_rounded, size: 18), label: const Text('Funções')),
          if (hasParent && !online)
            OutlinedButton.icon(onPressed: busy != null ? null : () => act('connect'), icon: const Icon(Icons.link_rounded, size: 18), label: const Text('Conectar')),
          if (online)
            OutlinedButton.icon(onPressed: busy != null ? null : () => act('disconnect'), icon: const Icon(Icons.link_off_rounded, size: 18), label: const Text('Desconectar')),
          if (!ro && n['role'] != 'Device')
            OutlinedButton.icon(onPressed: busy != null ? null : () => act('scan'), icon: const Icon(Icons.radar_rounded, size: 18), label: const Text('Buscar instrumentos')),
          if (!ro && n['role'] != 'Device')
            OutlinedButton.icon(onPressed: busy != null ? null : () => act('add'), icon: const Icon(Icons.add_rounded, size: 18), label: const Text('Adicionar dispositivo')),
          if (online)
            OutlinedButton.icon(
                onPressed: () => _showTraffic(context, state, n), icon: const Icon(Icons.swap_vert_rounded, size: 18), label: const Text('Tráfego')),
        ]),
        if (n['role'] == 'Device') ...[
          const SizedBox(height: 14),
          const SectionTitle('Parâmetros'),
          Wrap(spacing: 8, runSpacing: 8, children: [
            OutlinedButton.icon(onPressed: busy != null ? null : () => act('params'), icon: const Icon(Icons.tune_rounded, size: 18), label: const Text('Ver parâmetros')),
            OutlinedButton.icon(
                onPressed: busy != null || !online ? null : () => act('compare'),
                icon: const Icon(Icons.compare_arrows_rounded, size: 18),
                label: const Text('Comparar com o instrumento')),
            OutlinedButton.icon(
                onPressed: busy != null || !online || ro ? null : () => act('upload'),
                icon: const Icon(Icons.download_rounded, size: 18),
                label: const Text('Ler do instrumento')),
            OutlinedButton.icon(
                style: OutlinedButton.styleFrom(foregroundColor: AppTheme.danger, side: BorderSide(color: AppTheme.danger.withAlpha(online && n['policy'] == 'Full' ? 255 : 90))),
                onPressed: busy != null || !online || ro ? null : () => act('download'),
                icon: const Icon(Icons.upload_rounded, size: 18),
                label: const Text('Gravar no instrumento')),
          ]),
          if (!online)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text('Comparar, ler e gravar exigem o instrumento conectado. Gravar exige conexão em modo completo.',
                  style: TextStyle(color: AppTheme.textSecondary, fontSize: 12)),
            ),
        ],
        if (n['error'] != null) ...[
          const SizedBox(height: 14),
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
                color: AppTheme.danger.withAlpha(25), borderRadius: BorderRadius.circular(12), border: Border.all(color: AppTheme.danger.withAlpha(100))),
            child: Row(children: [
              Icon(Icons.error_outline_rounded, color: AppTheme.danger),
              const SizedBox(width: 10),
              Expanded(child: SelectableText('${n['error']}', style: TextStyle(color: AppTheme.text))),
            ]),
          ),
        ],
        const SizedBox(height: 22),
        const SectionTitle('Identificação'),
        KeyValue('Papel', roleLabel(n['role'] as String?)),
        KeyValue('Tipo de dispositivo', n['deviceType'] as String?),
        KeyValue('DTM', '${n['dtmName'] ?? ''}  (${n['vendor'] ?? ''})'),
        KeyValue('Ligado a', parent == null ? 'PC (raiz)' : '${parent['name']} · canal ${n['channelPath'] ?? ''}'),
        KeyValue('systemTag', tag, mono: true),
        KeyValue('CLSID do DTM', n['dtmClsid'] as String?, mono: true),
        const SizedBox(height: 14),
        const SectionTitle('Execução'),
        KeyValue('Processo do DTM', n['workerPid'] == null ? 'não carregado (sem consumo de memória)' : 'PID ${n['workerPid']}'),
        KeyValue('Tela aberta', n['uiOpen'] == true ? 'sim' : 'não'),
        KeyValue('Alterações não salvas', n['dirty'] == true ? 'sim' : 'não'),
      ]),
    );
  }

  static Future<void> _showTraffic(BuildContext context, AppState s, Json n) async {
    final log = await s.commLog(n['tag'] as String);
    if (!context.mounted) return;
    await showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text('Tráfego auditado · ${n['name']}'),
        content: SizedBox(
          width: 760,
          height: 460,
          child: log.isEmpty
              ? Center(child: Text('Nenhuma troca registrada.', style: TextStyle(color: AppTheme.textSecondary)))
              : ListView.builder(
                  itemCount: log.length,
                  itemBuilder: (_, i) {
                    final r = log[i];
                    final refused = r['forwarded'] != true;
                    return Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(children: [
                        SizedBox(width: 92, child: Text('${r['time']}', style: TextStyle(fontFamily: 'Consolas', fontSize: 12, color: AppTheme.textSecondary))),
                        SizedBox(width: 110, child: Text('${r['direction']}', style: TextStyle(fontSize: 12, color: AppTheme.textSecondary))),
                        Expanded(
                          child: Text('${r['method']} ${r['note'] ?? ''}',
                              style: TextStyle(fontSize: 12.5, color: refused ? AppTheme.danger : AppTheme.text, fontWeight: refused ? FontWeight.w600 : null)),
                        ),
                        if (refused) Pill('recusado', color: AppTheme.danger),
                      ]),
                    );
                  },
                ),
        ),
        actions: [TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Fechar'))],
      ),
    );
  }
}
