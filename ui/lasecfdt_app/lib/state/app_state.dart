import 'dart:async';
import 'dart:collection';
import 'dart:io';

import 'package:flutter/material.dart';

import '../core/app_theme.dart';
import '../core/host_client.dart';
import '../core/native_dock.dart';

typedef Json = Map<String, dynamic>;

/// Uma tela de DTM aberta (aba). A janela pertence ao processo worker do DTM; aqui só o identificador.
class DtmTab {
  DtmTab({required this.tag, required this.hwnd, required this.title, this.functionId, this.channelPath});
  final String tag;
  final int hwnd;
  String title;
  final String? functionId;
  final String? channelPath;
}

enum AppPage { project, catalog, events, plant }

/// Estado da UI de UMA estação. Toda regra de negócio fica no Host; aqui só espelho e comandos.
class AppState extends ChangeNotifier {
  AppState(this.host);
  final HostClient host;

  // ---------------------------------------------------------------- estação e backend
  bool ready = false;
  String? fatal;
  Json info = {};
  Json settings = {};
  int ownerHwnd = 0;
  AppPage page = AppPage.project;

  Json get station => (info['station'] as Json?) ?? {};
  bool get isRemote => station['isRemote'] == true;
  List<String> get warnings => ((info['warnings'] as List?) ?? []).cast<String>();

  // ---------------------------------------------------------------- catálogo
  List<Json> catalog = [];
  bool catalogLoading = false;
  Json? catalogMeta;

  // ---------------------------------------------------------------- projeto
  Json? project;
  final nodes = <String, Json>{};
  String? selectedTag;
  final busy = <String, String>{}; // tag → operação em andamento

  // ---------------------------------------------------------------- telas (abas) e eventos
  final tabs = <DtmTab>[];
  int activeTab = -1;
  final events = ListQueue<Json>();
  int unseenErrors = 0;
  static const maxEvents = 4000;

  final _messages = StreamController<(String, bool)>.broadcast();
  Stream<(String, bool)> get messages => _messages.stream;
  void toast(String m, {bool error = false}) => _messages.add((m, error));

  StreamSubscription<HostNotification>? _sub;

  /// Log da UI (%TEMP%\LasecFDT\ui.log; o TEMP é por sessão no servidor de terminal): diagnóstico de inicialização.
  static void log(String m) {
    try {
      final f = File('${Directory.systemTemp.path}\\LasecFDT\\ui.log');
      f.parent.createSync(recursive: true);
      f.writeAsStringSync('${DateTime.now().toIso8601String()} $m\n', mode: FileMode.append, flush: true);
    } catch (_) {}
  }

  Future<void> init() async {
    try {
      log('init: iniciando o Host');
      await host.start();
      log('init: Host iniciado (${host.executable})');
      _sub = host.notifications.listen(_onNotification);
      host.exitCode.then((code) {
        fatal = 'O motor da estação terminou (código $code). ${host.stderrText}'.trim();
        notifyListeners();
      });
      info = Map<String, dynamic>.from(await host.call('host.hello') as Map);
      log('init: hello ok');
      settings = Map<String, dynamic>.from(await host.call('station.settings') as Map);
      log('init: settings ok');
      _applySettings();
      ownerHwnd = await NativeDock.instance.hwnd();
      log('init: hwnd $ownerHwnd');
      ready = true;
      notifyListeners();
      unawaited(loadCatalog());
      final current = await host.call('project.current');
      if (current is Map && current['name'] != null) _setProject(Map<String, dynamic>.from(current));
    } catch (e, st) {
      log('init: ERRO $e\n$st');
      fatal = e.toString();
      notifyListeners();
    }
  }

  void _applySettings() {
    AppTheme.isLight = settings['theme'] == 'light';
    final reduced = settings['reducedEffects'];
    AppTheme.reducedEffects = reduced is bool ? reduced : isRemote;
  }

  bool get isLight => settings['theme'] == 'light';

  Future<void> updateSettings(Json changes) async {
    final next = {...settings, ...changes};
    settings = Map<String, dynamic>.from(await host.call('station.settings', next) as Map);
    _applySettings();
    notifyListeners();
  }

  void go(AppPage p) {
    if (page == p) return;
    page = p;
    if (p == AppPage.events) unseenErrors = 0;
    notifyListeners();
  }

  // ---------------------------------------------------------------- notificações do Host

  void _onNotification(HostNotification n) {
    if (n.method == 'nodeChanged') {
      final tag = n.params['tag'] as String?;
      if (tag == null || project == null || !nodes.containsKey(tag)) return;
      nodes[tag] = n.params;
      if (n.params['uiOpen'] != true) _dropTabsOf(tag);
      notifyListeners();
    } else if (n.method == 'event') {
      events.addFirst(n.params);
      while (events.length > maxEvents) {
        events.removeLast();
      }
      if (n.params['level'] == 'error' && page != AppPage.events) unseenErrors++;
      if (n.params['kind'] == 'host' && n.params['method'] == 'fatal') fatal = '${n.params['detail']}';
      notifyListeners();
    }
  }

  void _dropTabsOf(String tag) {
    final gone = tabs.where((t) => t.tag == tag).toList();
    if (gone.isEmpty) return;
    for (final t in gone) {
      NativeDock.instance.undock(t.hwnd);
      tabs.remove(t);
    }
    activeTab = tabs.isEmpty ? -1 : activeTab.clamp(0, tabs.length - 1);
  }

  // ---------------------------------------------------------------- catálogo

  Future<void> loadCatalog({bool refresh = false}) async {
    catalogLoading = true;
    notifyListeners();
    try {
      final c = Map<String, dynamic>.from(await host.call('catalog.list', {'refresh': refresh}) as Map);
      catalog = ((c['dtms'] as List?) ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList();
      catalogMeta = c..remove('dtms');
    } catch (e) {
      toast('Catálogo: $e', error: true);
    } finally {
      catalogLoading = false;
      notifyListeners();
    }
  }

  /// Tipos de dispositivo do DTM (cache da planta; na primeira vez o DTM é instanciado num worker descartável).
  Future<Json?> describe(String clsid) async {
    try {
      final d = Map<String, dynamic>.from(await host.call('catalog.describe', {'clsid': clsid}) as Map);
      final i = catalog.indexWhere((e) => e['clsid'] == clsid);
      if (i >= 0) {
        catalog[i] = {
          ...catalog[i],
          'deviceTypes': d['deviceTypes'],
          if ((d['name'] as String?)?.isNotEmpty == true) 'name': d['name'],
          if ((d['vendor'] as String?)?.isNotEmpty == true) 'vendor': d['vendor'],
          if ((d['version'] as String?)?.isNotEmpty == true) 'version': d['version'],
        };
        notifyListeners();
      }
      return d;
    } catch (e) {
      toast('Não foi possível ler o DTM: $e', error: true);
      return null;
    }
  }

  /// "Atualizar catálogo" completo: relê o registro e descreve todos os DTMs (cache comum a todas as estações).
  Future<void> describeAll(void Function(int done, int total) progress) async {
    await loadCatalog(refresh: true);
    final todo = catalog.where((e) => e['hostable'] == true && e['deviceTypes'] == null).toList();
    var done = 0;
    for (final e in todo) {
      await describe(e['clsid'] as String);
      progress(++done, todo.length);
    }
  }

  Json? catalogEntry(String? clsid) => catalog.cast<Json?>().firstWhere((e) => e!['clsid'] == clsid, orElse: () => null);

  // ---------------------------------------------------------------- projetos

  Future<List<Json>> listProjects() async =>
      ((await host.call('project.list')) as List).map((e) => Map<String, dynamic>.from(e as Map)).toList();

  void _setProject(Json? p) {
    for (final t in tabs) {
      NativeDock.instance.undock(t.hwnd);
    }
    tabs.clear();
    activeTab = -1;
    nodes.clear();
    selectedTag = null;
    project = p;
    if (p != null) {
      for (final n in (p['nodes'] as List? ?? [])) {
        final m = Map<String, dynamic>.from(n as Map);
        nodes[m['tag'] as String] = m;
      }
      selectedTag = roots.isNotEmpty ? roots.first['tag'] as String : null;
    }
    notifyListeners();
  }

  Future<bool> _run(Future<void> Function() op, {String? tag, String? label}) async {
    if (tag != null && label != null) {
      busy[tag] = label;
      notifyListeners();
    }
    try {
      await op();
      return true;
    } catch (e) {
      toast(e.toString(), error: true);
      return false;
    } finally {
      if (tag != null) busy.remove(tag);
      notifyListeners();
    }
  }

  Future<bool> newProject(String name) => _run(() async {
        _setProject(Map<String, dynamic>.from(await host.call('project.new', {'name': name}) as Map));
        toast('Projeto "$name" criado.');
      });

  Future<bool> openProject(String name) => _run(() async {
        final p = Map<String, dynamic>.from(await host.call('project.open', {'name': name}) as Map);
        _setProject(p);
        if (p['readOnly'] == true) toast('Aberto somente leitura: em edição na estação ${p['lockedBy']}.');
      });

  Future<bool> saveProject({String? saveAs}) => _run(() async {
        final p = Map<String, dynamic>.from(await host.call('project.save', saveAs == null ? null : {'name': saveAs}) as Map);
        project = {...p}..remove('nodes');
        for (final n in (p['nodes'] as List? ?? [])) {
          final m = Map<String, dynamic>.from(n as Map);
          nodes[m['tag'] as String] = m;
        }
        toast('Projeto "${p['name']}" salvo.');
      });

  Future<bool> closeProject() => _run(() async {
        await host.call('project.close');
        _setProject(null);
      });

  bool get projectReadOnly => project?['readOnly'] == true;
  bool get projectDirty => project?['dirty'] == true || nodes.values.any((n) => n['dirty'] == true);

  // ---------------------------------------------------------------- topologia

  List<Json> get roots => nodes.values.where((n) => n['parentTag'] == null || !nodes.containsKey(n['parentTag'])).toList();
  List<Json> childrenOf(String tag) => nodes.values.where((n) => n['parentTag'] == tag).toList();
  Json? get selected => selectedTag == null ? null : nodes[selectedTag];

  void select(String? tag) {
    selectedTag = tag;
    notifyListeners();
  }

  Future<bool> addNode({required String clsid, int deviceTypeIndex = 0, String? parentTag, String? name}) async {
    Json? added;
    final ok = await _run(() async {
      added = Map<String, dynamic>.from(await host.call('node.add', {
        'clsid': clsid,
        'deviceTypeIndex': deviceTypeIndex,
        'parentTag': ?parentTag,
        if (name != null && name.trim().isNotEmpty) 'name': name.trim(),
      }) as Map);
      nodes[added!['tag'] as String] = added!;
      selectedTag = added!['tag'] as String;
      project = {...?project, 'dirty': true};
    }, tag: parentTag ?? '_root', label: 'Adicionando…');
    if (ok) toast('${added!['name']} adicionado.');
    return ok;
  }

  Future<bool> removeNode(String tag) => _run(() async {
        await host.call('node.remove', {'tag': tag});
        final doomed = <String>[];
        void collect(String t) {
          doomed.add(t);
          for (final c in childrenOf(t)) {
            collect(c['tag'] as String);
          }
        }

        collect(tag);
        for (final t in doomed) {
          _dropTabsOf(t);
          nodes.remove(t);
        }
        if (doomed.contains(selectedTag)) selectedTag = roots.isNotEmpty ? roots.first['tag'] as String : null;
        project = {...?project, 'dirty': true};
      });

  Future<bool> renameNode(String tag, String name) => _run(() async {
        nodes[tag] = Map<String, dynamic>.from(await host.call('node.rename', {'tag': tag, 'name': name}) as Map);
        for (final t in tabs.where((t) => t.tag == tag)) {
          t.title = name;
        }
        project = {...?project, 'dirty': true};
      });

  Future<Json?> functions(String tag) async {
    Json? r;
    await _run(() async => r = Map<String, dynamic>.from(await host.call('node.functions', {'tag': tag}) as Map), tag: tag, label: 'Carregando DTM…');
    return r;
  }

  Future<bool> openUi(String tag, {String? functionId, String? channelPath, String? label}) async {
    final existing = tabs.indexWhere((t) => t.tag == tag && t.functionId == functionId && t.channelPath == channelPath && functionId != null);
    if (existing >= 0) {
      setActiveTab(existing);
      return true;
    }
    return _run(() async {
      // Um worker tem uma tela por vez: abrir outra função do mesmo nó substitui a aba dele.
      _dropTabsOf(tag);
      final r = Map<String, dynamic>.from(await host.call('node.openUi', {
        'tag': tag,
        'functionId': ?functionId,
        'channelPath': ?channelPath,
        'ownerHwnd': ownerHwnd,
      }) as Map);
      if (r['opened'] != true) throw HostError('A tela não abriu: ${r['error'] ?? 'motivo desconhecido'}');
      final node = nodes[tag]!;
      tabs.add(DtmTab(
        tag: tag,
        hwnd: (r['windowHandle'] as num).toInt(),
        title: '${node['name']} · ${label ?? r['functionName'] ?? 'Tela'}',
        functionId: functionId ?? r['functionId'] as String?,
        channelPath: channelPath,
      ));
      activeTab = tabs.length - 1;
      page = AppPage.project;
    }, tag: tag, label: 'Abrindo tela…');
  }

  void setActiveTab(int i) {
    if (i < 0 || i >= tabs.length) return;
    for (var k = 0; k < tabs.length; k++) {
      if (k != i) NativeDock.instance.hide(tabs[k].hwnd);
    }
    activeTab = i;
    selectedTag = tabs[i].tag;
    notifyListeners();
  }

  /// Volta à visão de detalhes (sem tela ativa): as telas continuam abertas nas abas.
  void showDetails() {
    for (final t in tabs) {
      NativeDock.instance.hide(t.hwnd);
    }
    activeTab = -1;
    notifyListeners();
  }

  Future<bool> closeTab(DtmTab t) => _run(() async {
        NativeDock.instance.undock(t.hwnd);
        tabs.remove(t);
        activeTab = tabs.isEmpty ? -1 : (activeTab >= tabs.length ? tabs.length - 1 : activeTab);
        notifyListeners();
        await host.call('node.closeUi', {'tag': t.tag});
      }, tag: t.tag, label: 'Fechando tela…');

  Future<bool> connect(String tag, {required String policy, bool confirmFull = false}) => _run(() async {
        nodes[tag] = Map<String, dynamic>.from(await host.call('node.connect', {'tag': tag, 'policy': policy, 'confirmFull': confirmFull}) as Map);
        toast('${nodes[tag]!['name']} conectado (${policyLabel(policy)}).');
      }, tag: tag, label: 'Conectando…');

  Future<bool> disconnect(String tag) => _run(() async {
        nodes[tag] = Map<String, dynamic>.from(await host.call('node.disconnect', {'tag': tag}) as Map);
      }, tag: tag, label: 'Desconectando…');

  Future<bool> unload(String tag) => _run(() async {
        _dropTabsOf(tag);
        nodes[tag] = Map<String, dynamic>.from(await host.call('node.unload', {'tag': tag}) as Map);
      }, tag: tag, label: 'Descarregando…');

  /// Busca de instrumentos no canal do nó (Host: node.scan). Lança HostError em falha (o diálogo mostra).
  Future<Json> scan(String tag) async {
    busy[tag] = 'Buscando instrumentos…';
    notifyListeners();
    try {
      return Map<String, dynamic>.from(await host.call('node.scan', {'tag': tag}, const Duration(minutes: 6)) as Map);
    } finally {
      busy.remove(tag);
      notifyListeners();
    }
  }

  // ---------------------------------------------------------------- parâmetros (comandados pelo Frame)

  Future<Json> _busyCall(String tag, String label, String method, Map<String, dynamic> params) async {
    busy[tag] = label;
    notifyListeners();
    try {
      return Map<String, dynamic>.from(await host.call(method, params, const Duration(minutes: 7)) as Map);
    } finally {
      busy.remove(tag);
      notifyListeners();
    }
  }

  /// Parâmetros atuais do DTM (IDtmParameter::GetParameters).
  Future<Json> parameters(String tag) => _busyCall(tag, 'Lendo parâmetros do DTM…', 'node.parameters', {'tag': tag});

  /// Compara o projeto com o instrumento; o projeto é restaurado ao final (nada muda).
  Future<Json> compare(String tag) => _busyCall(tag, 'Comparando com o instrumento…', 'node.compare', {'tag': tag});

  /// Lê do instrumento e mantém os valores lidos no DTM (dá para restaurar o projeto depois).
  Future<Json> upload(String tag) => _busyCall(tag, 'Lendo do instrumento…', 'node.upload', {'tag': tag});

  Future<bool> revert(String tag) => _run(() async {
        nodes[tag] = Map<String, dynamic>.from(await host.call('node.revert', {'tag': tag}) as Map);
        toast('Valores do projeto restaurados em ${nodes[tag]!['name']}.');
      }, tag: tag, label: 'Restaurando o projeto…');

  /// Grava no instrumento (exige conexão "completo" e confirmação; o Host confere de novo e audita).
  Future<Json> download(String tag) => _busyCall(tag, 'Gravando no instrumento…', 'node.download', {'tag': tag, 'confirm': true});

  Future<List<Json>> commLog(String tag) async =>
      ((await host.call('node.commLog', {'tag': tag})) as List).map((e) => Map<String, dynamic>.from(e as Map)).toList();

  Future<List<Json>> leases() async =>
      ((await host.call('plant.leases')) as List).map((e) => Map<String, dynamic>.from(e as Map)).toList();

  static String policyLabel(String? p) => switch (p) {
        'Full' => 'completo — permite escrita',
        'Blocked' => 'bloqueado',
        _ => 'somente leitura',
      };

  @override
  void dispose() {
    _sub?.cancel();
    _messages.close();
    super.dispose();
  }
}
