import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';

const infra = path.join(__dirname, '..', '..');

// FUP-13: infra-tests.yml runs jest once per folder (--testPathPatterns=unit, snapshot and
// integration). A test file outside them ran in no CI step: fayda-mock.test.ts did until it moved
// into unit/.
describe('FUP-13: every infra test runs in a CI step', () => {
  const workflow = fs.readFileSync(path.join(infra, '..', '.github', 'workflows', 'infra-tests.yml'), 'utf8');
  const patterns = [...workflow.matchAll(/--testPathPatterns=(\S+)/g)].map((m) => m[1]);

  function testFiles(dir: string): string[] {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        return entry.name === '__snapshots__' ? [] : testFiles(full);
      }
      return entry.name.endsWith('.test.ts') ? [path.relative(infra, full).split(path.sep).join('/')] : [];
    });
  }

  test('the workflow runs the unit, snapshot and integration folders', () => {
    expect([...patterns].sort()).toEqual(['integration', 'snapshot', 'unit']);
  });

  test('each test file matches one of the patterns', () => {
    const files = testFiles(path.join(infra, 'test'));
    expect(files).toContain('test/unit/fayda-mock.test.ts');
    for (const file of files) {
      expect([file, patterns.some((p) => new RegExp(p).test(file))]).toEqual([file, true]);
    }
  });
});

// FUP-57: two TypeScript compilers are installed. `npm run lint` and `npm run build` call the
// `tsc` bin, which must be TS 7 (`@typescript/native`, the native compiler). ts-jest and ts-node
// (cdk synth) load the `typescript` package, the TS 6 alias (`@typescript/typescript6`), because TS 7
// has no JS API for them. That alias re-exports its dependency `@typescript/old` (TS 6), which ships
// a `tsc` bin too, so two packages claim `.bin/tsc`. npm 10 links bins in order of depth, then path,
// and the first link wins (arborist rebuild.js, bin-links link-gently.js): `@typescript/native`
// sorts before `@typescript/old`, so `tsc` is TS 7. That rests on the alias name; these tests fail
// if the link ever lands on the TS 6 package.
describe('FUP-57: which tsc the lint runs', () => {
  const modules = path.join(infra, 'node_modules');
  const manifest = (name: string) =>
    JSON.parse(fs.readFileSync(path.join(modules, name, 'package.json'), 'utf8')) as {
      name: string;
      version: string;
      bin?: Record<string, string>;
    };

  test('lint and build call the tsc bin', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(infra, 'package.json'), 'utf8')) as { scripts: Record<string, string> };
    expect(pkg.scripts.lint).toMatch(/(^|&& )tsc --noEmit$/);
    expect(pkg.scripts.build).toBe('tsc');
  });

  test('the tsc bin links to the native compiler package', () => {
    const bin = fs.realpathSync(path.join(modules, '.bin', 'tsc'));
    const native = fs.realpathSync(path.join(modules, '@typescript', 'native'));
    expect(bin.startsWith(native + path.sep)).toBe(true);
    expect(Object.keys(manifest('@typescript/native').bin ?? {})).toContain('tsc');
  });

  test('the typescript package ts-jest loads is the TS 6 alias, not the compiler lint runs', () => {
    expect(manifest('typescript').name).toBe('@typescript/typescript6');
    expect(ts.version).toMatch(/^6\./);
    expect(ts.version).not.toBe(manifest('@typescript/native').version);
  });
});
