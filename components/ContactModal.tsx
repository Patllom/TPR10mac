'use client';

import React, { useState } from 'react';
import { X, CheckCircle2, Send, ShieldCheck } from 'lucide-react';

interface ContactModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export default function ContactModal({ isOpen, onClose }: ContactModalProps) {
  const [submitted, setSubmitted] = useState(false);
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    phone: '',
    organization: '',
    serviceType: 'Water Resources & Telemetering (RID/DWR)',
    message: '',
  });

  if (!isOpen) return null;

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitted(true);
  };

  const handleReset = () => {
    setSubmitted(false);
    setFormData({
      name: '',
      email: '',
      phone: '',
      organization: '',
      serviceType: 'Water Resources & Telemetering (RID/DWR)',
      message: '',
    });
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 bg-black/70 backdrop-blur-md animate-fade-in">
      <div className="relative w-full max-w-lg rounded-3xl glass-panel p-6 sm:p-8 shadow-2xl border border-orange-500/30 bg-white/95 dark:bg-slate-950/95 max-h-[90vh] overflow-y-auto">
        {/* Close Button */}
        <button
          onClick={onClose}
          className="absolute top-5 right-5 p-2 rounded-full text-slate-500 hover:text-slate-950 dark:text-slate-400 dark:hover:text-white hover:bg-black/5 dark:hover:bg-white/10 transition-colors"
          aria-label="Close dialog"
        >
          <X className="w-5 h-5" />
        </button>

        {submitted ? (
          <div className="py-8 flex flex-col items-center text-center">
            <div className="w-16 h-16 rounded-full bg-orange-500/10 text-orange-500 flex items-center justify-center mb-6">
              <CheckCircle2 className="w-8 h-8" />
            </div>
            <h3 className="text-2xl font-bold text-slate-900 dark:text-white mb-2">
              Inquiry Sent Successfully
            </h3>
            <p className="text-sm text-slate-600 dark:text-slate-400 leading-relaxed max-w-sm mb-8">
              Thank you, {formData.name || 'Partner'}. The technical team at TPR-10 Co., Ltd. has received your request and will contact you promptly.
            </p>
            <button
              onClick={handleReset}
              className="px-6 py-2.5 rounded-xl text-xs font-bold bg-gradient-to-r from-orange-500 to-amber-600 text-white shadow-md hover:from-orange-600 hover:to-amber-700 transition-colors"
            >
              Done
            </button>
          </div>
        ) : (
          <div>
            {/* Header */}
            <div className="mb-6">
              <div className="inline-flex items-center gap-1.5 text-xs font-mono text-orange-600 dark:text-orange-400 font-bold mb-2">
                <ShieldCheck className="w-4 h-4" />
                <span>TPR-10 CO., LTD. // DIRECT INQUIRY</span>
              </div>
              <h3 className="text-2xl font-black text-slate-950 dark:text-white">
                Contact TPR-10 Team
              </h3>
              <p className="text-xs sm:text-sm text-slate-600 dark:text-slate-400 mt-1">
                Consult with our specialists for Telemetering, Digital &amp; AI, and Infrastructure Solutions.
              </p>
            </div>

            {/* Form */}
            <form onSubmit={handleSubmit} className="space-y-4">
              <div>
                <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                  FULL NAME / CONTACT PERSON *
                </label>
                <input
                  type="text"
                  required
                  value={formData.name}
                  onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                  placeholder="e.g. Somchai Prasert"
                  className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-orange-500/40"
                />
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                    EMAIL ADDRESS *
                  </label>
                  <input
                    type="email"
                    required
                    value={formData.email}
                    onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                    placeholder="name@organization.go.th"
                    className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-orange-500/40"
                  />
                </div>

                <div>
                  <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                    PHONE NUMBER
                  </label>
                  <input
                    type="tel"
                    value={formData.phone}
                    onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                    placeholder="08X-XXX-XXXX"
                    className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-orange-500/40"
                  />
                </div>
              </div>

              <div>
                <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                  ORGANIZATION / AGENCY
                </label>
                <input
                  type="text"
                  value={formData.organization}
                  onChange={(e) => setFormData({ ...formData, organization: e.target.value })}
                  placeholder="e.g. Royal Irrigation Dept / Private Enterprise"
                  className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-orange-500/40"
                />
              </div>

              <div>
                <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                  SERVICE OF INTEREST
                </label>
                <select
                  value={formData.serviceType}
                  onChange={(e) => setFormData({ ...formData, serviceType: e.target.value })}
                  className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-orange-500/40"
                >
                  <option value="Water Resources & Telemetering (RID/DWR)">Water Resources &amp; Telemetering Support (RID/DWR)</option>
                  <option value="Web & Application Development">Web &amp; Application Development</option>
                  <option value="AI Solutions & Smart Automation">AI Solutions &amp; Smart Automation</option>
                  <option value="Infrastructure, Network & Security">Infrastructure, Network &amp; Security</option>
                  <option value="Maintenance & Spare Parts Support">Maintenance &amp; Spare Parts Support</option>
                </select>
              </div>

              <div>
                <label className="block text-xs font-mono font-bold text-slate-700 dark:text-slate-300 mb-1.5">
                  PROJECT SCOPE / MESSAGE
                </label>
                <textarea
                  rows={3}
                  value={formData.message}
                  onChange={(e) => setFormData({ ...formData, message: e.target.value })}
                  placeholder="Describe your requirements, station locations, or system timeline..."
                  className="w-full px-4 py-2.5 rounded-xl text-xs sm:text-sm bg-slate-100/80 dark:bg-slate-900 border border-black/5 dark:border-white/10 text-slate-900 dark:text-white placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-orange-500/40 resize-none"
                />
              </div>

              <div className="pt-2">
                <button
                  type="submit"
                  className="w-full py-3.5 rounded-xl text-xs sm:text-sm font-bold bg-gradient-to-r from-orange-500 to-amber-600 hover:from-orange-600 hover:to-amber-700 text-white transition-all duration-200 shadow-md shadow-orange-500/25 flex items-center justify-center gap-2"
                >
                  <Send className="w-4 h-4" />
                  <span>Submit Inquiry to TPR-10</span>
                </button>
              </div>
            </form>
          </div>
        )}
      </div>
    </div>
  );
}
