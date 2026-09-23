import type { Metadata } from 'next';
import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ServiceUnavailable } from '@/components/auth/AuthFrame';

export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: 'TPR10 Portal | ระบบปฏิบัติการภายใน',
  description: 'พื้นที่ทำงานสำหรับพนักงาน TPR-10',
  robots: { index: false, follow: false },
};

export default async function PortalLayout({ children }: { children: React.ReactNode }) {
  try { await requirePortalSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  return <main lang="th" className="min-h-screen flex items-center justify-center px-6 py-16">{children}</main>;
}
