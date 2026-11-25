import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../state/app_state.dart';
import 'catalog_page.dart';
import 'events_page.dart';
import 'plant_page.dart';
import 'project_page.dart';
import 'widgets.dart';

/// Casca da aplicação: navegação lateral (Projeto, Catálogo, Eventos, Estação) + cabeçalho fixo com o projeto,
/// a estação e o seletor de tema — a mesma organização das telas de módulo do Gestor-UFU.
class Shell extends StatelessWidget {
  const Shell({required this.state, super.key});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Container(
        decoration: AppTheme.background,
        child: SafeArea(
          child: ListenableBuilder(
            listenable: state,
            builder: (context, _) {
              if (state.closing) {
                return EmptyState(
                  icon: Icons.power_settings_new_rounded,
                  title: 'Encerrando o LasecFDT…',
                  message: 'Interrompendo o que estava em andamento e liberando os DTMs desta estação.',
                  action: busySpinner(size: 26),
                );
              }
              if (state.fatal != null && !state.ready) return _Fatal(state: state);
              if (!state.ready) {
                return EmptyState(
                  icon: Icons.hub_rounded,
                  title: 'Iniciando o motor da estação…',
                  message: 'O LasecFDT.Host é o backend desta estação: hospeda os DTMs em processos isolados.',
                  action: busySpinner(size: 26),
                );
              }
              return Row(children: [
                _NavRail(state: state),
                VerticalDivider(width: 1, color: AppTheme.divider),
                Expanded(
                  child: Column(children: [
                    _Header(state: state),
                    if (state.fatal != null) _Banner(text: state.fatal!, color: AppTheme.danger),
                    for (final w in state.warnings) _Banner(text: w, color: AppTheme.warning),
                    Expanded(
                      child: IndexedStack(
                        index: state.page.index,
                        children: [
                          ProjectPage(state: state),
                          CatalogPage(state: state),
                          EventsPage(state: state),
                          PlantPage(state: state),
                        ],
                      ),
                    ),
                  ]),
                ),
              ]);
            },
          ),
        ),
      ),
    );
  }
}

class _NavRail extends StatelessWidget {
  const _NavRail({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    Widget item(AppPage p, IconData icon, String label, {int badge = 0}) {
      final sel = state.page == p;
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Tooltip(
          message: label,
          preferBelow: false,
          child: InkWell(
            borderRadius: BorderRadius.circular(14),
            onTap: () => state.go(p),
            child: Container(
              width: 76,
              padding: const EdgeInsets.symmetric(vertical: 10),
              decoration: BoxDecoration(
                color: sel ? AppTheme.primary.withAlpha(AppTheme.isLight ? 30 : 45) : Colors.transparent,
                borderRadius: BorderRadius.circular(14),
              ),
              child: Column(children: [
                Badge(
                  isLabelVisible: badge > 0,
                  label: Text('$badge'),
                  backgroundColor: AppTheme.danger,
                  child: Icon(icon, color: sel ? AppTheme.primary : AppTheme.textSecondary),
                ),
                const SizedBox(height: 4),
                Text(label,
                    style: TextStyle(
                        fontSize: 11.5, fontWeight: sel ? FontWeight.w700 : FontWeight.w500, color: sel ? AppTheme.text : AppTheme.textSecondary)),
              ]),
            ),
          ),
        ),
      );
    }

    return Container(
      width: 92,
      color: AppTheme.surface.withAlpha(AppTheme.reducedEffects ? 255 : 150),
      padding: const EdgeInsets.symmetric(vertical: 14),
      child: Column(children: [
        Container(
          width: 46,
          height: 46,
          decoration: BoxDecoration(gradient: AppTheme.primaryGradient, borderRadius: BorderRadius.circular(14)),
          child: const Icon(Icons.hub_rounded, color: Colors.white, size: 26),
        ),
        const SizedBox(height: 6),
        Text('LasecFDT', style: TextStyle(fontSize: 11, fontWeight: FontWeight.w800, color: AppTheme.text)),
        const SizedBox(height: 18),
        item(AppPage.project, Icons.account_tree_rounded, 'Projeto'),
        item(AppPage.catalog, Icons.inventory_2_rounded, 'Catálogo'),
        item(AppPage.events, Icons.receipt_long_rounded, 'Eventos', badge: state.unseenErrors),
        item(AppPage.plant, Icons.dns_rounded, 'Estação'),
        const Spacer(),
        Tooltip(
          message: state.isLight ? 'Tema escuro' : 'Tema claro',
          child: IconButton(
            onPressed: () => state.updateSettings({'theme': state.isLight ? 'dark' : 'light'}),
            icon: Icon(state.isLight ? Icons.dark_mode_rounded : Icons.light_mode_rounded, color: AppTheme.textSecondary),
          ),
        ),
      ]),
    );
  }
}

class _Header extends StatelessWidget {
  const _Header({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    final p = state.project;
    final st = state.station;
    final (icon, title, subtitle) = switch (state.page) {
      AppPage.project => (
          Icons.account_tree_rounded,
          p == null ? 'Projeto' : p['name'] as String,
          p == null ? 'Nenhum projeto aberto' : '${state.nodes.length} nó(s) na topologia'
        ),
      AppPage.catalog => (Icons.inventory_2_rounded, 'Catálogo de DTMs', '${state.catalog.length} DTMs instalados neste servidor'),
      AppPage.events => (Icons.receipt_long_rounded, 'Eventos', 'Eventos do Frame, dos DTMs e da comunicação desta estação'),
      AppPage.plant => (Icons.dns_rounded, 'Estação e planta', 'Identidade, preferências e recursos compartilhados'),
    };
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 14, 20, 10),
      child: Row(children: [
        IconBadge(icon, color: AppTheme.primary),
        const SizedBox(width: 12),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
            Row(children: [
              Flexible(
                child: Text(title, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 21, fontWeight: FontWeight.bold, color: AppTheme.text)),
              ),
              if (state.page == AppPage.project && p != null) ...[
                if (state.projectDirty) ...[
                  const SizedBox(width: 10),
                  Tooltip(message: 'Alterações não salvas', child: Icon(Icons.circle, size: 9, color: AppTheme.warning)),
                ],
                if (state.projectReadOnly) ...[
                  const SizedBox(width: 10),
                  Pill('Somente leitura · em edição em ${p['lockedBy']}', color: AppTheme.warning, icon: Icons.lock_rounded),
                ],
              ],
            ]),
            Text(subtitle, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, color: AppTheme.textSecondary)),
          ]),
        ),
        Tooltip(
          message: 'Sessão ${st['sessionId']} · ${st['user']}${state.isRemote ? ' · sessão remota' : ''}',
          child: Pill('Estação ${st['id'] ?? '?'}', color: AppTheme.accent, icon: state.isRemote ? Icons.cast_connected_rounded : Icons.computer_rounded),
        ),
      ]),
    );
  }
}

class _Banner extends StatelessWidget {
  const _Banner({required this.text, required this.color});
  final String text;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.fromLTRB(20, 0, 20, 8),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        decoration: BoxDecoration(color: color.withAlpha(30), borderRadius: BorderRadius.circular(12), border: Border.all(color: color.withAlpha(110))),
        child: Row(children: [
          Icon(Icons.warning_amber_rounded, color: color, size: 18),
          const SizedBox(width: 10),
          Expanded(child: Text(text, style: TextStyle(color: AppTheme.text, fontSize: 13))),
        ]),
      );
}

class _Fatal extends StatelessWidget {
  const _Fatal({required this.state});
  final AppState state;

  @override
  Widget build(BuildContext context) => EmptyState(
        icon: Icons.error_outline_rounded,
        title: 'O motor da estação não iniciou',
        message: state.fatal,
      );
}
