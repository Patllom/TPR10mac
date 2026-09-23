import Link from 'next/link';

export default function AuthFrame({ title, children }: { title: string; children: React.ReactNode }) {
  return <main lang="th" className="min-h-screen grid place-items-center px-4 py-12">
    <section className="w-full max-w-xl rounded-3xl border border-orange-500/30 bg-white dark:bg-slate-900 p-6 sm:p-10 shadow-xl">
      <p className="text-sm font-semibold text-orange-600">TPR-10 · ระบบภายใน</p>
      <h1 className="my-6 text-2xl font-bold">{title}</h1>
      <noscript><p role="alert">ต้องเปิด JavaScript เพื่อใช้ระบบเข้าสู่ระบบอย่างปลอดภัย</p></noscript>
      {children}
      <Link href="/" className="mt-8 inline-block underline focus-visible:outline focus-visible:outline-2">กลับหน้าหลัก</Link>
    </section>
  </main>;
}
export function ServiceUnavailable() {
  return <AuthFrame title="บริการเข้าสู่ระบบไม่พร้อมใช้งาน">
    <p role="alert">ยังตรวจสอบ session ไม่ได้ กรุณาลองใหม่ภายหลัง</p>
    <a href="/login" className="mt-5 inline-block underline">ลองใหม่</a>
  </AuthFrame>;
}
