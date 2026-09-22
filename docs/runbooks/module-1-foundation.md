# คู่มือรัน Module 1

## เครื่องมือ

- .NET SDK ตาม global.json และ Docker Engine ที่ทำงานแล้ว
- Node.js และ npm; แนะนำ Node รุ่นที่รองรับ dependency ปัจจุบัน (เครื่องทดสอบใช้ 20.18.0 และมี engine warning จาก eslint-visitor-keys)
- `dotnet tool restore` สำหรับ EF CLI
- เครื่องที่พัฒนารอบนี้ใช้ SDK ที่ /private/tmp/tpr10-dotnet ต้องเพิ่มไดเรกทอรีนี้ใน PATH และตั้ง DOTNET_ROOT หากยังไม่ได้ติดตั้ง SDK ถาวร; ไฟล์ใน /private/tmp อาจถูกล้างได้

## ฐานข้อมูล local

คัดลอก backend/.env.foundation.example เป็น backend/.env.foundation และเปลี่ยนรหัสผ่าน local ทั้งสองค่าให้ตรงกัน ไฟล์จริงถูก ignore ห้ามนำ secret เข้า Git

```bash
docker compose --env-file backend/.env.foundation -f backend/docker-compose.foundation.yml up -d postgres
dotnet tool restore
```

ตั้ง TPR10_CONNECTION_STRING ใน environment ของ terminal อย่างปลอดภัยตามค่าในไฟล์ local แล้วรัน:

```bash
sh ops/migrations/apply-local.sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project backend/src/TPR10.Api --urls http://127.0.0.1:5080
```

API อ่าน TPR10_CONNECTION_STRING จาก configuration ไม่รับ secret ผ่าน CLI argument; ไม่รัน migration อัตโนมัติตอน start

Compose สำหรับ local ใช้ account เจ้าของฐานข้อมูลเพื่อความสะดวกเท่านั้น Production ต้องแยก migration owner กับ runtime role ซึ่งไม่มีสิทธิ์ ALTER/DROP/TRUNCATE; trigger ไม่ใช่การป้องกันผู้ดูแลฐานข้อมูล

## เว็บ

```bash
npm ci
TPR10_API_ORIGIN=http://127.0.0.1:5080 npm run dev -- --hostname 127.0.0.1
```

Development ใช้ port 4000 ตรวจ /, /portal และ /api/health/live บน origin เดียวกัน

```bash
TPR10_API_ORIGIN=http://127.0.0.1:5080 npm run build
TPR10_API_ORIGIN=http://127.0.0.1:5080 npm run start -- --hostname 127.0.0.1
```

Production process ใช้ port 4001 และต้องกำหนด origin ตอน build ด้วย เพราะ Next.js บันทึก rewrite ใน build output หากใช้ Nginx แยก /api อยู่แล้ว สามารถ build โดยไม่ตั้ง origin ได้

## Reverse proxy

รัน Nginx บน host เดียวกับเว็บ/API แล้ว render ไปไฟล์ใหม่:

```bash
TPR10_WEB_UPSTREAM=127.0.0.1:4001 TPR10_API_UPSTREAM=127.0.0.1:5080 sh infra/nginx/render-config.sh /private/tmp/tpr10-proxy-new.conf
```

renderer ใช้ Node ที่มีอยู่แล้วและแทนเฉพาะสองตัวแปรของ TPR10 จึงคง $host และตัวแปร Nginx ไว้ครบ ไฟล์ปลายทางต้องยังไม่มีอยู่เพื่อไม่เขียนทับ config เดิม

template นี้เป็น HTTP local foundation; ก่อน deploy จริง Operations ต้องกำหนด hostname/TLS/firewall ตาม Module 0 ส่วน /api ต้องคง prefix เมื่อ proxy ไป API ไม่เปิด CORS

API เชื่อ forwarded headers เฉพาะ 127.0.0.1 และ ::1 และจำกัดหนึ่ง proxy hop หากย้าย Nginx ไปอีกเครื่อง/container ต้องเพิ่ม IP ที่เชื่อถือได้แบบเจาะจงและทดสอบก่อน deploy

บน Docker Desktop ใช้ host.docker.internal สำหรับ routing smoke test ได้ แต่ไม่ถือว่าทดสอบ TLS หรือ trusted proxy topology ของ production แล้ว

## การทดสอบ

```bash
npm test
npm run lint
npm run build
dotnet test backend/TPR10.sln -c Release
dotnet build backend/TPR10.sln -c Release --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
docker compose --env-file backend/.env.foundation -f backend/docker-compose.foundation.yml config --quiet
```

Integration tests สร้าง PostgreSQL container ชั่วคราวเอง ไม่ใช้ฐานข้อมูลธุรกิจ ครอบคลุม migration up/down/up, immutable audit event/metadata, database outage, correlation, validation และ transaction rollback เมื่อ audit insert ถูกปฏิเสธ

## Rollback

สคริปต์รับเฉพาะ loopback port 54329 database tpr10 username tpr10_app และปฏิเสธ key ซ้ำหรือไม่รองรับ ก่อนใช้งานตรวจฐานข้อมูลเป้าหมายและยอมรับการสูญเสียข้อมูล local:

```bash
sh ops/migrations/rollback-local.sh 0
sh ops/migrations/apply-local.sh
```

คำสั่งย้อนถึง 0 ลบตาราง foundation ทั้งหมด ใช้กับ local/test ที่ทิ้งได้เท่านั้น Production ต้องมี backup ที่ทดสอบ restore, SQL script ที่ review แล้ว, maintenance window และผู้รับผิดชอบ ห้ามใช้สคริปต์ local นี้กับ production

## ขอบเขต

/portal เป็นหน้าเตรียมเปิดใช้งาน ยังไม่มี login/session/RBAC หรือข้อมูลธุรกิจ Technical probe เปิดเฉพาะ Development/Testing; ใช้ Production เมื่อเปิดบริการให้ผู้ใช้งาน และคง development API บน loopback
