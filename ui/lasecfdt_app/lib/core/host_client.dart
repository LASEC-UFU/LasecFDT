import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// Erro devolvido pelo LasecFDT.Host (mensagem já pronta para o operador).
class HostError implements Exception {
  HostError(this.message, [this.code = 0]);
  final String message;
  final int code;
  @override
  String toString() => message;
}

/// Notificação do Host (sem resposta): "event" (HostEventDto) ou "nodeChanged" (NodeDto).
class HostNotification {
  HostNotification(this.method, this.params);
  final String method;
  final Map<String, dynamic> params;
}

/// Cliente do backend da estação. O Host é um processo FILHO desta UI e conversa por stdin/stdout (uma mensagem
/// JSON por linha): o canal é anônimo, não tem nome que outra sessão do servidor possa abrir, e o Host morre com a
/// UI. Uma UI = um Host = uma estação.
class HostClient {
  Process? _process;
  int _nextId = 0;
  final _pending = <int, Completer<dynamic>>{};
  final _notifications = StreamController<HostNotification>.broadcast();
  final _exit = Completer<int>();
  final _stderr = StringBuffer();

  Stream<HostNotification> get notifications => _notifications.stream;
  Future<int> get exitCode => _exit.future;
  bool get running => _process != null && !_exit.isCompleted;
  String get stderrText => _stderr.toString();
  String? executable;

  /// Localiza o LasecFDT.Host.exe: LASECFDT_HOST; pasta "engine" ao lado do executável da UI (instalação);
  /// ou, em desenvolvimento, a saída de compilação dentro da solução LasecFDT.
  static String? locate() {
    final env = Platform.environment['LASECFDT_HOST'];
    if (env != null && File(env).existsSync()) return env;
    final exeDir = File(Platform.resolvedExecutable).parent;
    final installed = File('${exeDir.path}\\engine\\LasecFDT.Host.exe');
    if (installed.existsSync()) return installed.path;
    Directory? dir = exeDir;
    while (dir != null) {
      if (File('${dir.path}\\LasecFDT.sln').existsSync()) {
        for (final cfg in ['Release', 'Debug']) {
          final f = File('${dir.path}\\src\\LasecFDT.Host\\bin\\$cfg\\net48\\LasecFDT.Host.exe');
          if (f.existsSync()) return f.path;
        }
        return null;
      }
      final parent = dir.parent;
      if (parent.path == dir.path) break;
      dir = parent;
    }
    return null;
  }

  Future<void> start({List<String> args = const []}) async {
    final exe = locate();
    if (exe == null) {
      throw HostError('LasecFDT.Host.exe não encontrado. Defina LASECFDT_HOST ou instale o motor na pasta "engine".');
    }
    executable = exe;
    final p = await Process.start(exe, args, workingDirectory: File(exe).parent.path);
    _process = p;
    p.stdout.transform(utf8.decoder).transform(const LineSplitter()).listen(_onLine, onDone: () {});
    p.stderr.transform(utf8.decoder).listen((s) {
      if (_stderr.length < 20000) _stderr.write(s);
    });
    p.exitCode.then((code) {
      if (!_exit.isCompleted) _exit.complete(code);
      for (final c in _pending.values) {
        if (!c.isCompleted) c.completeError(HostError('O motor da estação (LasecFDT.Host) terminou (código $code).'));
      }
      _pending.clear();
    });
  }

  void _onLine(String line) {
    if (line.trim().isEmpty) return;
    Map<String, dynamic> m;
    try {
      m = jsonDecode(line) as Map<String, dynamic>;
    } catch (_) {
      return;
    }
    final id = m['id'];
    final method = m['method'];
    if (id != null && method == null) {
      final c = _pending.remove((id as num).toInt());
      if (c == null) return;
      final err = m['error'];
      if (err != null) {
        c.completeError(HostError('${err['message']}', (err['code'] as num?)?.toInt() ?? 0));
      } else {
        c.complete(m['result']);
      }
    } else if (method != null) {
      final params = m['params'];
      _notifications.add(HostNotification(method as String, params is Map<String, dynamic> ? params : <String, dynamic>{}));
    }
  }

  /// Chama um método do Host. Operações com DTM podem levar segundos (o DTM inicializa num processo próprio).
  Future<dynamic> call(String method, [Object? params, Duration timeout = const Duration(minutes: 5)]) {
    final p = _process;
    if (p == null || _exit.isCompleted) return Future.error(HostError('O motor da estação não está em execução.'));
    final id = ++_nextId;
    final c = Completer<dynamic>();
    _pending[id] = c;
    final msg = <String, dynamic>{'id': id, 'method': method};
    if (params != null) msg['params'] = params;
    p.stdin.add(utf8.encode('${jsonEncode(msg)}\n'));
    return c.future.timeout(timeout, onTimeout: () {
      _pending.remove(id);
      throw HostError('Tempo esgotado em $method.');
    });
  }

  /// Fecha o stdin: o Host libera todos os DTMs pelo protocolo e termina.
  Future<void> stop({Duration wait = const Duration(seconds: 30)}) async {
    final p = _process;
    if (p == null) return;
    try {
      await p.stdin.close();
    } catch (_) {}
    try {
      await exitCode.timeout(wait);
    } catch (_) {
      p.kill();
    }
  }
}
