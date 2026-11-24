import 'package:flutter/material.dart';

import '../core/app_theme.dart';

/// Cartão padrão (gradiente sutil como no Gestor-UFU; liso com efeitos reduzidos).
class AppCard extends StatelessWidget {
  const AppCard({required this.child, this.padding = const EdgeInsets.all(18), this.border, super.key});
  final Widget child;
  final EdgeInsets padding;
  final Color? border;

  @override
  Widget build(BuildContext context) {
    Theme.of(context);
    return Container(decoration: AppTheme.cardDecoration(border: border), padding: padding, child: child);
  }
}

/// Ícone em "pílula" colorida (cabeçalhos, itens de navegação).
class IconBadge extends StatelessWidget {
  const IconBadge(this.icon, {required this.color, this.size = 22, super.key});
  final IconData icon;
  final Color color;
  final double size;

  @override
  Widget build(BuildContext context) => Container(
        padding: EdgeInsets.all(size * 0.38),
        decoration: BoxDecoration(color: color.withAlpha(40), borderRadius: BorderRadius.circular(12)),
        child: Icon(icon, color: color, size: size),
      );
}

class SectionTitle extends StatelessWidget {
  const SectionTitle(this.text, {this.trailing, super.key});
  final String text;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Row(children: [
          Expanded(
            child: Text(text, style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: AppTheme.text)),
          ),
          ?trailing,
        ]),
      );
}

class Pill extends StatelessWidget {
  const Pill(this.text, {required this.color, this.icon, super.key});
  final String text;
  final Color color;
  final IconData? icon;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 3),
        decoration: BoxDecoration(
          color: color.withAlpha(AppTheme.isLight ? 28 : 40),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: color.withAlpha(90)),
        ),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          if (icon != null) ...[Icon(icon, size: 13, color: color), const SizedBox(width: 4)],
          Text(text, style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: color)),
        ]),
      );
}

/// Ícone e cor por papel do nó na topologia.
IconData roleIcon(String? role) => switch (role) {
      'Communication' => Icons.cable_rounded,
      'Gateway' => Icons.router_rounded,
      _ => Icons.sensors_rounded,
    };

Color roleColor(String? role) => switch (role) {
      'Communication' => AppTheme.accent,
      'Gateway' => AppTheme.warning,
      _ => AppTheme.primary,
    };

String roleLabel(String? role) => switch (role) {
      'Communication' => 'Comunicação',
      'Gateway' => 'Gateway',
      _ => 'Dispositivo',
    };

/// Estado de execução do nó (vem do Host).
Color stateColor(String? s) => switch (s) {
      'online' => AppTheme.green,
      'connecting' || 'loading' => AppTheme.warning,
      'loaded' => AppTheme.accent,
      'error' => AppTheme.danger,
      _ => AppTheme.textSecondary,
    };

String stateLabel(String? s) => switch (s) {
      'online' => 'Online',
      'connecting' => 'Conectando',
      'loading' => 'Carregando',
      'loaded' => 'Carregado',
      'error' => 'Erro',
      _ => 'Offline',
    };

class StateDot extends StatelessWidget {
  const StateDot(this.state, {this.size = 9, super.key});
  final String? state;
  final double size;

  @override
  Widget build(BuildContext context) => Container(
        width: size,
        height: size,
        decoration: BoxDecoration(color: stateColor(state), shape: BoxShape.circle),
      );
}

class EmptyState extends StatelessWidget {
  const EmptyState({required this.icon, required this.title, this.message, this.action, super.key});
  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 460),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            IconBadge(icon, color: AppTheme.primary, size: 34),
            const SizedBox(height: 16),
            Text(title, textAlign: TextAlign.center, style: TextStyle(fontSize: 18, fontWeight: FontWeight.w700, color: AppTheme.text)),
            if (message != null) ...[
              const SizedBox(height: 8),
              Text(message!, textAlign: TextAlign.center, style: TextStyle(color: AppTheme.textSecondary, height: 1.4)),
            ],
            if (action != null) ...[const SizedBox(height: 18), action!],
          ]),
        ),
      );
}

class KeyValue extends StatelessWidget {
  const KeyValue(this.label, this.value, {this.mono = false, super.key});
  final String label;
  final String? value;
  final bool mono;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          SizedBox(width: 150, child: Text(label, style: TextStyle(color: AppTheme.textSecondary, fontSize: 13))),
          Expanded(
            child: SelectableText(
              (value == null || value!.isEmpty) ? '—' : value!,
              style: TextStyle(color: AppTheme.text, fontSize: 13, fontFamily: mono ? 'Consolas' : null),
            ),
          ),
        ]),
      );
}

Widget busySpinner({double size = 16, Color? color}) => SizedBox(
      width: size,
      height: size,
      child: CircularProgressIndicator(strokeWidth: 2, color: color ?? AppTheme.primary),
    );
