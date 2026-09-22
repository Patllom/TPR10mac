import Link from 'next/link';

export default function PortalPage() {
  return (
    <section className="w-full max-w-xl rounded-3xl border border-orange-500/20 bg-white/80 dark:bg-slate-900/80 p-8 sm:p-12 shadow-xl">
      <p className="text-sm font-semibold tracking-widest text-orange-600 dark:text-orange-400">TPR10 PORTAL</p>
      <h1 className="mt-4 text-3xl font-bold text-slate-950 dark:text-white">ระบบปฏิบัติการภายใน</h1>
      <p className="mt-5 leading-relaxed text-slate-600 dark:text-slate-300">
        พื้นที่ทำงานสำหรับพนักงานกำลังเตรียมเปิดใช้งาน ระบบเข้าสู่ระบบจะพร้อมให้ใช้งานในระยะถัดไป
      </p>
      <Link href="/" className="mt-8 inline-flex rounded-full border border-orange-500/40 px-5 py-3 text-sm font-semibold text-orange-700 dark:text-orange-300 hover:bg-orange-500/10">
        กลับหน้าหลัก
      </Link>
    </section>
  );
}
