'use client';

import React from 'react';
import { ArrowRight, Sparkles, PhoneCall } from 'lucide-react';

interface CTASectionProps {
  onOpenContact: () => void;
}

export default function CTASection({ onOpenContact }: CTASectionProps) {
  return (
    <section className="py-20 px-4 sm:px-6 max-w-7xl mx-auto">
      <div className="relative rounded-3xl overflow-hidden glass-panel p-8 sm:p-14 border border-orange-500/30 shadow-2xl bg-gradient-to-br from-white/90 via-orange-500/5 to-amber-500/10 dark:from-slate-950 dark:via-slate-900 dark:to-orange-950/30">
        {/* Glow */}
        <div className="absolute -top-24 -right-24 w-96 h-96 rounded-full bg-orange-500/15 blur-3xl pointer-events-none" />
        <div className="absolute -bottom-24 -left-24 w-96 h-96 rounded-full bg-cyan-500/15 blur-3xl pointer-events-none" />

        <div className="relative z-10 flex flex-col items-center text-center max-w-3xl mx-auto">
          <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono tracking-wider glass-pill text-orange-600 dark:text-orange-400 border border-orange-500/20 mb-5">
            <Sparkles className="w-3.5 h-3.5" />
            <span>PARTNER WITH TPR-10</span>
          </div>

          <h2 className="text-3xl sm:text-5xl font-black tracking-tight text-slate-950 dark:text-white leading-[1.12] mb-5">
            Ready to deploy resilient technology &amp; telemetry solutions?
          </h2>

          <p className="text-sm sm:text-base text-slate-600 dark:text-slate-300 leading-relaxed mb-8 max-w-2xl">
            Whether you need telemetry maintenance for government water resources, custom AI application platforms, or secure enterprise networks, our engineering team is ready to support your organization.
          </p>

          <div className="flex flex-wrap items-center justify-center gap-4 w-full sm:w-auto">
            <button
              onClick={onOpenContact}
              className="w-full sm:w-auto px-8 py-4 rounded-xl text-xs sm:text-sm font-bold bg-gradient-to-r from-orange-500 to-amber-600 hover:from-orange-600 hover:to-amber-700 text-white transition-all duration-300 shadow-xl shadow-orange-500/25 flex items-center justify-center gap-2 group active:scale-98"
            >
              <span>Get in Touch with TPR-10</span>
              <ArrowRight className="w-4 h-4 transition-transform duration-300 group-hover:translate-x-1" />
            </button>

            <a
              href="#telemetry"
              className="w-full sm:w-auto px-8 py-4 rounded-xl text-xs sm:text-sm font-semibold glass-panel text-slate-800 dark:text-slate-200 hover:bg-orange-500/10 transition-all duration-200 flex items-center justify-center gap-2"
            >
              <PhoneCall className="w-4 h-4 text-orange-500" />
              <span>Review Services</span>
            </a>
          </div>
        </div>
      </div>
    </section>
  );
}
