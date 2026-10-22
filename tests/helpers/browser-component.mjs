import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { resolve, dirname } from 'node:path';
import ts from 'typescript';

// Mount the real component and React in an isolated browser, without a Next server or database.
export function browserComponent(entry) {
  const modules = [], ids = new Map();
  function add(file) {
    if (ids.has(file)) return ids.get(file);
    const id = modules.length; ids.set(file, id); modules.push('');
    const require = createRequire(file);
    let code = readFileSync(file, 'utf8');
    if (/\.tsx?$/.test(file)) code = ts.transpileModule(code, { compilerOptions: {
      module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX,
    } }).outputText;
    code = code.replace(/require\(["']([^"']+)["']\)/g, (_, name) => {
      const target = name.startsWith('@/') ? resolve(name.slice(2)) : name.startsWith('.') ? resolve(dirname(file), name) : null;
      const path = target ? /\.[cm]?js$/.test(target) ? target : target + '.ts' : require.resolve(name);
      return `require(${add(path)})`;
    });
    modules[id] = `function(require,module,exports){${code}\n}`;
    return id;
  }
  const component = add(resolve(entry));
  const require = createRequire(resolve(entry));
  const react = add(require.resolve('react')), dom = add(require.resolve('react-dom/client'));
  return `(()=>{const process={env:{NODE_ENV:'development'}}, modules=[${modules.join(',')}],cache={};
    function require(id){if(!cache[id]){const m=cache[id]={exports:{}};modules[id](require,m,m.exports);}return cache[id].exports;}
    const root=require(${dom}).createRoot(document.getElementById('root'));
    window.renderEditor=props=>root.render(require(${react}).createElement(require(${component}).default,props));})();`;
}
