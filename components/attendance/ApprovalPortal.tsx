'use client';
import { useState } from 'react';
import CorrectionPortal from './CorrectionPortal';
export default function ApprovalPortal() {
  const [stage, setStage] = useState<'supervisor' | 'hr'>('supervisor');
  return <div className="min-w-0 space-y-5"><label className="block">ขั้นพิจารณา<select className="auth-input w-full" value={stage} onChange={e => setStage(e.target.value as typeof stage)}><option value="supervisor">หัวหน้า</option><option value="hr">HR</option></select></label><CorrectionPortal key={stage} audience={stage} /></div>;
}
