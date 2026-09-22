import { readFileSync, writeFileSync } from 'node:fs';
const values = {};
const https = process.argv[3] === '--https';
for (const name of ['TPR10_WEB_UPSTREAM', 'TPR10_API_UPSTREAM']) {
  const value = process.env[name] ?? '';
  const match = /^([a-zA-Z0-9.-]+):([0-9]{1,5})$/.exec(value);
  if (!match || Number(match[2]) < 1 || Number(match[2]) > 65535) {
    console.error(name + ' ต้องเป็น host:port ที่ถูกต้อง');
    process.exit(1);
  }
  values[name] = value;
}
if (https) {
  if (!/:(4000|4001)$/.test(values.TPR10_WEB_UPSTREAM)
      || !/^127\.0\.0\.1:[0-9]+$/.test(values.TPR10_API_UPSTREAM)) {
    console.error('HTTPS local ต้องใช้ web port 4000/4001 และ API loopback ใน network namespace เดียวกัน');
    process.exit(1);
  }
  for (const name of ['TPR10_TLS_CERT', 'TPR10_TLS_KEY']) {
    const value = process.env[name] ?? '';
    if (!/^\/[a-zA-Z0-9_./-]+$/.test(value)) {
      console.error(name + ' ต้องเป็น absolute path ที่ไม่มีช่องว่างหรือ directive');
      process.exit(1);
    }
    values[name] = value;
  }
}
const target = process.argv[2];
if (!target) {
  console.error('ต้องระบุไฟล์ปลายทางใหม่');
  process.exit(1);
}
let output = readFileSync(new URL(https ? './identity-local-https.conf.template' : './default.conf.template', import.meta.url), 'utf8');
for (const [key, value] of Object.entries(values))
  output = output.replaceAll('${' + key + '}', value);
writeFileSync(target, output, { flag: 'wx', mode: 0o600 });
