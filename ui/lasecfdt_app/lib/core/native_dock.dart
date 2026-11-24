import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

/// Ponte com o runner nativo (windows/runner/flutter_window.cpp): HWND desta janela e encaixe das telas dos DTMs
/// (janelas de outros processos) sobre a área de conteúdo.
class NativeDock {
  NativeDock._();
  static final instance = NativeDock._();
  static const _channel = MethodChannel('lasecfdt/native');

  int _hwnd = 0;
  final _rects = <int, Rect>{};
  final _visible = <int, bool>{};
  int _overlays = 0;

  Future<int> hwnd() async {
    if (_hwnd != 0) return _hwnd;
    try {
      _hwnd = (await _channel.invokeMethod<int>('hwnd')) ?? 0;
    } catch (_) {
      _hwnd = 0; // testes / outra plataforma
    }
    return _hwnd;
  }

  Future<void> dock(int hwnd, Rect r, {required bool visible}) async {
    if (hwnd == 0) return;
    if (_rects[hwnd] == r && _visible[hwnd] == visible) return;
    _rects[hwnd] = r;
    _visible[hwnd] = visible;
    try {
      await _channel.invokeMethod('dock', {
        'hwnd': hwnd,
        'x': r.left,
        'y': r.top,
        'w': r.width,
        'h': r.height,
        'visible': visible && _overlays == 0,
      });
    } catch (_) {}
  }

  Future<void> hide(int hwnd) async {
    final r = _rects[hwnd];
    if (r == null || _visible[hwnd] == false) return;
    await dock(hwnd, r, visible: false);
  }

  Future<void> undock(int hwnd) async {
    _rects.remove(hwnd);
    _visible.remove(hwnd);
    try {
      await _channel.invokeMethod('undock', {'hwnd': hwnd});
    } catch (_) {}
  }

  /// Diálogos/menus do Flutter ficam por baixo das janelas dos DTMs: enquanto houver algum aberto, elas somem.
  Future<void> overlayOpened() async {
    _overlays++;
    if (_overlays == 1) {
      try {
        await _channel.invokeMethod('hideAll', {'hide': true});
      } catch (_) {}
    }
  }

  Future<void> overlayClosed() async {
    if (_overlays == 0) return;
    _overlays--;
    if (_overlays == 0) {
      try {
        await _channel.invokeMethod('hideAll', {'hide': false});
      } catch (_) {}
    }
  }

  Future<void> activate() async {
    try {
      await _channel.invokeMethod('activate');
    } catch (_) {}
  }
}

/// Observa a pilha de rotas: diálogos, menus e listas suspensas (PopupRoute) escondem as telas dos DTMs.
class DockOverlayObserver extends NavigatorObserver {
  @override
  void didPush(Route route, Route? previousRoute) {
    if (route is PopupRoute) NativeDock.instance.overlayOpened();
  }

  @override
  void didPop(Route route, Route? previousRoute) {
    if (route is PopupRoute) NativeDock.instance.overlayClosed();
  }

  @override
  void didRemove(Route route, Route? previousRoute) {
    if (route is PopupRoute) NativeDock.instance.overlayClosed();
  }
}

/// Área reservada para a tela de um DTM. Mede a própria posição a cada quadro e a envia ao runner.
class DockSlot extends StatefulWidget {
  const DockSlot({required this.hwnd, required this.visible, super.key});
  final int hwnd;
  final bool visible;

  @override
  State<DockSlot> createState() => _DockSlotState();
}

class _DockSlotState extends State<DockSlot> with WidgetsBindingObserver {
  final _key = GlobalKey();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _schedule();
  }

  @override
  void didUpdateWidget(covariant DockSlot oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.hwnd != widget.hwnd) NativeDock.instance.hide(oldWidget.hwnd);
    _schedule();
  }

  @override
  void didChangeMetrics() => _schedule();

  void _schedule() => WidgetsBinding.instance.addPostFrameCallback((_) => _report());

  void _report() {
    if (!mounted) return;
    final box = _key.currentContext?.findRenderObject() as RenderBox?;
    if (box == null || !box.hasSize) return;
    final origin = box.localToGlobal(Offset.zero);
    NativeDock.instance.dock(widget.hwnd, origin & box.size, visible: widget.visible);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    NativeDock.instance.hide(widget.hwnd);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // Reporta também quando o tamanho muda sem mudar as métricas da janela (ex.: painel redimensionado).
    return LayoutBuilder(builder: (context, constraints) {
      _schedule();
      return SizedBox.expand(key: _key);
    });
  }
}
