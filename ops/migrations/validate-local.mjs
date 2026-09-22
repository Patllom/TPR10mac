const fail = () => {
  console.error('ปลายทางฐานข้อมูล local ไม่ถูกต้อง: อนุญาตเฉพาะ tpr10 ที่ loopback:54329');
  process.exit(1);
};
const raw = process.env.TPR10_CONNECTION_STRING;
if (!raw) fail();
const values = {};
for (const part of raw.split(';').filter(Boolean)) {
  const separator = part.indexOf('=');
  if (separator < 1) fail();
  const key = part.slice(0, separator).trim().toLowerCase();
  const value = part.slice(separator + 1).trim();
  if (!['host', 'port', 'database', 'username', 'password', 'timeout'].includes(key) || key in values) fail();
  values[key] = value;
}
if (!['127.0.0.1', 'localhost', '::1'].includes(values.host) ||
    values.port !== '54329' || values.database !== 'tpr10' || values.username !== 'tpr10_app') fail();
