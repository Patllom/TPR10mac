import { readFileSync, writeFileSync } from 'node:fs';
const values = {};
for (const name of ['TPR10_WEB_UPSTREAM', 'TPR10_API_UPSTREAM']) {
  const value = process.env[name] ?? '';
  const match = /^([a-zA-Z0-9.-]+):([0-9]{1,5})$/.exec(value);
  if (!match || Number(match[2]) < 1 || Number(match[2]) > 65535) {
    console.error(name + ' ต้องเป็น host:port ที่ถูกต้อง');
    process.exit(1);
  }
  values[name] = value;
}
const target = process.argv[2];
if (!target) {
  console.error('ต้องระบุไฟล์ปลายทางใหม่');
  process.exit(1);
}
let output = readFileSync(new URL('./default.conf.template', import.meta.url), 'utf8');
for (const [key, value] of Object.entries(values))
  output = output.replaceAll('${' + key + '}', value);
writeFileSync(target, output, { flag: 'wx', mode: 0o600 });
