import type { Metadata } from 'next';

export const metadata: Metadata = {
  title: 'TPR10 Portal | ระบบปฏิบัติการภายใน',
  description: 'พื้นที่ทำงานสำหรับพนักงาน TPR-10',
  robots: { index: false, follow: false },
};

export default function PortalLayout({ children }: { children: React.ReactNode }) {
  return <main lang="th" className="min-h-screen flex items-center justify-center px-6 py-16">{children}</main>;
}
