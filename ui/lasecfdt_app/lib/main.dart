import 'dart:async';
import 'dart:ui';

import 'package:flutter/material.dart';

import 'core/app_theme.dart';
import 'core/host_client.dart';
import 'core/native_dock.dart';
import 'state/app_state.dart';
import 'ui/shell.dart';
import 'ui/update_dialog.dart';

final messengerKey = GlobalKey<ScaffoldMessengerState>();
final navigatorKey = GlobalKey<NavigatorState>();

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final state = AppState(HostClient());
  runApp(LasecFdtApp(state: state));
  unawaited(state.init());
}

class LasecFdtApp extends StatefulWidget {
  const LasecFdtApp({required this.state, super.key});
  final AppState state;

  @override
  State<LasecFdtApp> createState() => _LasecFdtAppState();
}

class _LasecFdtAppState extends State<LasecFdtApp> {
  late final AppLifecycleListener _lifecycle;
  StreamSubscription<(String, bool)>? _toasts;
  bool _updateChecked = false;

  @override
  void initState() {
    super.initState();
    // Ao abrir (motor pronto): se houver versão nova no GitHub, pergunta se pode atualizar (doc 11 §13).
    widget.state.addListener(_maybeCheckUpdates);
    _toasts = widget.state.messages.listen((m) {
      messengerKey.currentState
        ?..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(
          content: Text(m.$1),
          backgroundColor: m.$2 ? AppTheme.danger : null,
          duration: Duration(seconds: m.$2 ? 8 : 3),
          showCloseIcon: true,
        ));
    });
    // Ao fechar a janela: o Host libera todos os DTMs pelo protocolo antes de o processo terminar.
    // Fechar no meio de uma operação não espera por ela: o Host interrompe como o botão Parar (doc 11 §12).
    // A tela "Encerrando…" aparece na hora; as janelas dos DTMs somem para não cobri-la.
    _lifecycle = AppLifecycleListener(onExitRequested: () async {
      if (widget.state.closing) return AppExitResponse.cancel; // segundo clique no X: o primeiro já está encerrando
      widget.state.beginClose();
      unawaited(NativeDock.instance.overlayOpened());
      await widget.state.host.stop(wait: const Duration(seconds: 20));
      return AppExitResponse.exit;
    });
  }

  void _maybeCheckUpdates() {
    if (_updateChecked || !widget.state.ready) return;
    _updateChecked = true;
    widget.state.removeListener(_maybeCheckUpdates);
    unawaited(checkForUpdates(() => navigatorKey.currentContext ?? context, widget.state));
  }

  @override
  void dispose() {
    widget.state.removeListener(_maybeCheckUpdates);
    _toasts?.cancel();
    _lifecycle.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.state,
      builder: (context, _) {
        final light = widget.state.isLight;
        return MaterialApp(
          title: 'LasecFDT',
          debugShowCheckedModeBanner: false,
          scaffoldMessengerKey: messengerKey,
          navigatorKey: navigatorKey,
          theme: AppTheme.build(light: true),
          darkTheme: AppTheme.build(light: false),
          themeMode: light ? ThemeMode.light : ThemeMode.dark,
          themeAnimationDuration: AppTheme.reducedEffects ? Duration.zero : const Duration(milliseconds: 200),
          navigatorObservers: [DockOverlayObserver()],
          builder: (context, child) => MediaQuery(
            // Efeitos reduzidos (sessão remota): sem animações de transição.
            data: MediaQuery.of(context).copyWith(disableAnimations: AppTheme.reducedEffects),
            child: child!,
          ),
          // A chave reconstrói a árvore quando o tema ou os efeitos mudam: as cores vêm de AppTheme (estático) e
          // widgets const não seriam redesenhados.
          home: Shell(key: ValueKey('$light-${AppTheme.reducedEffects}'), state: widget.state),
        );
      },
    );
  }
}
