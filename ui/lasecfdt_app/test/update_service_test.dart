import 'package:flutter_test/flutter_test.dart';
import 'package:lasecfdt_ui/core/update_service.dart';

void main() {
  test('normaliza a tag do GitHub, a versão do motor e a da UI', () {
    expect(UpdateService.normalizeVersion('v0.1.3'), '0.1.3');
    expect(UpdateService.normalizeVersion('0.1.2.0'), '0.1.2');
    expect(UpdateService.normalizeVersion('0.1.1+7869'), '0.1.1');
    expect(UpdateService.normalizeVersion('dev'), isNull);
  });

  test('só oferece versão estritamente mais nova', () {
    expect(UpdateService.compareVersions('0.1.3', '0.1.2.0'), greaterThan(0));
    expect(UpdateService.compareVersions('0.1.2', '0.1.2.0'), 0);
    expect(UpdateService.compareVersions('0.1.10', '0.1.9'), greaterThan(0));
    expect(UpdateService.compareVersions('0.1.2', '0.2.0'), lessThan(0));
    // Versão desconhecida do motor conta como 0.0.0: qualquer release é mais nova.
    expect(UpdateService.compareVersions('0.1.2', ''), greaterThan(0));
  });

  test('novidades do Release em texto simples', () {
    const md = '## ⚠️ Antes de usar\n\n**Instalador:** [Releases](https://github.com/x) e <https://a.b/c>.\n- item `x`';
    expect(UpdateService.plainNotes(md),
        '⚠️ Antes de usar\n\nInstalador: Releases (https://github.com/x) e https://a.b/c.\n•  item x');
  });
}
