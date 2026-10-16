import 'dart:io';
import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:macropilot_installer/services/installation_files.dart';

void main() {
  late Directory root;
  setUp(() { root = Directory.systemTemp.createTempSync('mp_install_test_'); });
  tearDown(() { if (root.existsSync()) root.deleteSync(recursive: true); });

  test('strict startup handshake requires token and explicit parent-child handoff', () {
    final handoff = InstallationFiles.parseStartupSignal('token:handoff:42:43', 'token', 42);
    expect(handoff, (handoff: true, pid: 43));
    expect(InstallationFiles.parseStartupSignal('token:43', 'token', handoff!.pid), (handoff: false, pid: 43));
    for (final value in ['other:42','token:43','token:handoff:99:43','token:handoff:42:0','token:handoff:42:-1','token:handoff:42:42']) {
      expect(InstallationFiles.parseStartupSignal(value,'token',42), isNull);
    }
  });

  test('uninstall deletes owned files and keeps user files and custom data', () {
    final app = Directory(p.join(root.path, 'MacroPilot'))..createSync();
    File(p.join(app.path,'MacroPilot.exe')).writeAsStringSync('app');
    File(p.join(app.path,'personal.txt')).writeAsStringSync('personal');
    Directory(p.join(app.path,'data')).createSync();
    File(p.join(app.path,'data','plans.json')).writeAsStringSync('plans');
    InstallationFiles.writeOwned(app.path, ['MacroPilot.exe']);
    expect(InstallationFiles.deleteOwned(app.path, InstallationFiles.owned(app.path)), isEmpty);
    expect(File(p.join(app.path,'personal.txt')).readAsStringSync(), 'personal');
    expect(File(p.join(app.path,'data','plans.json')).readAsStringSync(), 'plans');
    expect(File(p.join(app.path,'MacroPilot.exe')).existsSync(), isFalse);
  });

  test('legacy installation only owns the explicit fallback list', () {
    File(p.join(root.path,'MacroPilot.exe')).writeAsStringSync('app');
    File(p.join(root.path,'notes.txt')).writeAsStringSync('mine');
    final owned = InstallationFiles.owned(root.path, legacy: ['MacroPilot.exe']);
    expect(owned, {'MacroPilot.exe'});
    InstallationFiles.deleteOwned(root.path, owned);
    expect(File(p.join(root.path,'notes.txt')).existsSync(), isTrue);
  });

  test('path traversal and drive roots are rejected', () {
    expect(() => InstallationFiles.child(root.path, '../outside.txt'), throwsFormatException);
    expect(() => InstallationFiles.child(root.path, 'C:/outside.txt'), throwsFormatException);
    expect(() => InstallationFiles.validateTarget(p.rootPrefix(root.path)), throwsA(isA<FileSystemException>()));
  });

  test('invalid ownership never expands deletion outside installation', () {
    File(p.join(root.path, InstallationFiles.manifestName)).writeAsStringSync('{"schema":1,"files":["../outside.txt"]}');
    expect(() => InstallationFiles.owned(root.path), throwsFormatException);
  });

  test('new package cannot claim and overwrite an existing unknown user file', () {
    File(p.join(root.path,'notes.txt')).writeAsStringSync('my notes');
    expect(() => InstallationFiles.rejectCollisions(root.path, ['notes.txt']), throwsA(isA<FileSystemException>()));
    expect(File(p.join(root.path,'notes.txt')).readAsStringSync(), 'my notes');
    File(p.join(root.path,'MacroPilot.exe')).writeAsStringSync('legacy application');
    expect(() => InstallationFiles.rejectCollisions(root.path, ['MacroPilot.exe']), returnsNormally);
  });

  test('prepare and failed install leave the original intact', () {
    final app = Directory(p.join(root.path,'app'))..createSync();
    File(p.join(app.path,'personal.txt')).writeAsStringSync('original');
    final transaction = InstallTransaction(app.path)..prepare();
    File(p.join(transaction.stage,'personal.txt')).writeAsStringSync('partial');
    transaction.rollback();
    expect(File(p.join(app.path,'personal.txt')).readAsStringSync(), 'original');
    expect(Directory(transaction.stage).existsSync(), isFalse);
  });

  test('backup remains until successful startup confirmation', () {
    final app = Directory(p.join(root.path,'app'))..createSync();
    File(p.join(app.path,'personal.txt')).writeAsStringSync('user data');
    final transaction = InstallTransaction(app.path)..prepare()..commit();
    expect(File(p.join(transaction.backup,'personal.txt')).readAsStringSync(), 'user data');
    expect(File(p.join(app.path,'personal.txt')).readAsStringSync(), 'user data');
    transaction.confirm();
    expect(Directory(transaction.backup).existsSync(), isFalse);
    expect(File(p.join(app.path,'personal.txt')).readAsStringSync(), 'user data');
  });

  test('startup failure rolls back while retaining files written by failed new app', () {
    final app = Directory(p.join(root.path,'app'))..createSync();
    File(p.join(app.path,'MacroPilot.exe')).writeAsStringSync('old');
    final transaction = InstallTransaction(app.path)..prepare();
    File(p.join(transaction.stage,'MacroPilot.exe')).writeAsStringSync('new');
    transaction.commit();
    File(p.join(app.path,'created.txt')).writeAsStringSync('new data');
    transaction.rollback();
    expect(File(p.join(app.path,'MacroPilot.exe')).readAsStringSync(), 'old');
    expect(File(p.join('${app.path}.failed_${transaction.token}','created.txt')).readAsStringSync(), 'new data');
  });

  test('locked elevated new app prevents rollback without deleting either side', () {
    final app = Directory(p.join(root.path,'app'))..createSync();
    File(p.join(app.path,'MacroPilot.exe')).writeAsStringSync('old');
    final transaction = InstallTransaction(app.path)..prepare();
    File(p.join(transaction.stage,'MacroPilot.exe')).writeAsStringSync('new');
    transaction.commit();
    expect(() => transaction.rollback(targetInUse: true), throwsA(isA<FileSystemException>()));
    expect(File(p.join(app.path,'MacroPilot.exe')).readAsStringSync(), 'new');
    expect(File(p.join(transaction.backup,'MacroPilot.exe')).readAsStringSync(), 'old');
  });

  test('fresh install also has a pending startup transaction', () {
    final transaction = InstallTransaction(p.join(root.path,'fresh'))..prepare()..commit();
    expect(File(p.join(transaction.target,InstallationFiles.pendingName)).existsSync(), isTrue);
    expect(transaction.backedUp, isFalse);
  });
}
