import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve, dirname } from 'node:path';
import ts from 'typescript';

export function loadTs(path) {
  const absolute = resolve(path);
  const output = ts.transpileModule(readFileSync(absolute, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }
  }).outputText;
  const module = { exports: {} };
  const require = createRequire(absolute);
  new Function('require', 'module', 'exports', output)(
    name => name.startsWith('.') ? loadTs(resolve(dirname(absolute), name + '.ts')) : require(name), module, module.exports);
  return module.exports;
}
