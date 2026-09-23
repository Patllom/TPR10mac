import AuthFrame from '@/components/auth/AuthFrame';
import ResetForm from './ResetForm';

export const dynamic = 'force-dynamic';
export default function ResetPage() {
  return <AuthFrame title="กู้คืนรหัสผ่าน"><ResetForm /></AuthFrame>;
}
