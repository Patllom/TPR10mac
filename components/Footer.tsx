'use client';

import React, { useState, useEffect } from 'react';
import Link from 'next/link';
import { Clock, ShieldCheck, ArrowUp } from 'lucide-react';

export default function Footer() {
  const [bkkTime, setBkkTime] = useState('');

  useEffect(() => {
    const updateTimes = () => {
      const now = new Date();
      setBkkTime(
        now.toLocaleTimeString('th-TH', {
          timeZone: 'Asia/Bangkok',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
          hour12: false,
        })
      );
    };

    updateTimes();
    const interval = setInterval(updateTimes, 1000);
    return () => clearInterval(interval);
  }, []);

  const scrollToTop = () => {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  };

  return (
    <footer className="w-full border-t border-orange-500/15 dark:border-white/10 pt-16 pb-12 px-4 sm:px-6 max-w-7xl mx-auto">
      {/* Live HQ Operations Bar */}
      <div className="rounded-2xl glass-panel p-6 mb-12 border border-orange-500/20 flex flex-col sm:flex-row items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-orange-500/10 text-orange-600 dark:text-orange-400 flex items-center justify-center">
            <Clock className="w-5 h-5" />
          </div>
          <div>
            <div className="text-xs font-mono font-bold text-slate-900 dark:text-white uppercase tracking-wider">
              TPR-10 HEADQUARTERS &amp; OPERATIONS CENTER
            </div>
            <div className="text-xs text-slate-500 dark:text-slate-400">
              Bangkok, Thailand (UTC+7) • 24/7 Telemetry Dispatch
            </div>
          </div>
        </div>

        <div className="flex items-center gap-3">
          <div className="text-right font-mono">
            <div className="text-xs text-slate-400">OPERATIONAL TIME</div>
            <div className="text-lg font-bold text-orange-600 dark:text-orange-400">
              {bkkTime || '--:--:--'}
            </div>
          </div>
          <span className="w-2.5 h-2.5 rounded-full bg-emerald-500 animate-pulse" />
        </div>
      </div>

      {/* Main Footer Body */}
      <div className="grid grid-cols-1 md:grid-cols-4 gap-8 pb-12 border-b border-black/5 dark:border-white/10">
        {/* Brand Info */}
        <div className="md:col-span-2">
          <div className="flex items-center gap-2.5 mb-4">
            <div className="w-8 h-8 rounded-xl bg-gradient-to-br from-orange-500 to-amber-600 text-white flex items-center justify-center font-black text-xs">
              TPR
            </div>
            <span className="font-black text-base tracking-tight text-slate-950 dark:text-white">
              TPR-10 CO., LTD.
            </span>
          </div>
          <p className="text-xs sm:text-sm text-slate-600 dark:text-slate-400 leading-relaxed max-w-md mb-4">
            Integrated IT, Network, AI, and Water Information Solutions. Delivering mission-critical telemetry, software development, and infrastructure for government agencies and enterprise organizations.
          </p>
          <div className="inline-flex items-center gap-2 text-xs font-mono text-emerald-600 dark:text-emerald-400">
            <ShieldCheck className="w-4 h-4" />
            <span>Certified Telemetry &amp; System Integration Standards</span>
          </div>
        </div>

        {/* Services Navigation */}
        <div>
          <h4 className="text-xs font-mono uppercase tracking-widest text-slate-950 dark:text-white font-bold mb-3">
            CORE SERVICES
          </h4>
          <ul className="space-y-2 text-xs text-slate-600 dark:text-slate-400">
            <li><a href="#telemetry" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">RID &amp; DWR Telemetering Projects</a></li>
            <li><a href="#telemetry" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Telemetry Operation &amp; Maintenance</a></li>
            <li><a href="#digital-ai" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Web &amp; App Development</a></li>
            <li><a href="#digital-ai" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Enterprise AI Solutions</a></li>
            <li><a href="#infrastructure" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Private Network &amp; Security</a></li>
          </ul>
        </div>

        {/* Corporate & Support */}
        <div>
          <h4 className="text-xs font-mono uppercase tracking-widest text-slate-950 dark:text-white font-bold mb-3">
            GOVERNMENT &amp; ENTERPRISE
          </h4>
          <ul className="space-y-2 text-xs text-slate-600 dark:text-slate-400">
            <li><a href="#telemetry" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Royal Irrigation Department (RID)</a></li>
            <li><a href="#telemetry" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Department of Water Resources (DWR)</a></li>
            <li><a href="#infrastructure" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Industrial Next-Gen Firewalls</a></li>
            <li><a href="#overview" className="hover:text-orange-600 dark:hover:text-orange-400 transition-colors">Why Choose TPR-10</a></li>
          </ul>
        </div>
      </div>

      {/* Bottom Bar */}
      <Link href="/login" prefetch={false} className="inline-block mt-6 text-xs text-slate-500 hover:text-orange-600 dark:text-slate-400">
        เข้าสู่ระบบพนักงาน
      </Link>
      <div className="pt-8 flex flex-col sm:flex-row items-center justify-between gap-4 text-xs text-slate-500 dark:text-slate-400 font-mono">
        <div>
          &copy; {new Date().getFullYear()} TPR-10 CO., LTD. ALL RIGHTS RESERVED.
        </div>
        <button
          onClick={scrollToTop}
          className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg glass-panel hover:bg-orange-500/10 text-slate-700 dark:text-slate-300 transition-colors"
        >
          <span>Top</span>
          <ArrowUp className="w-3.5 h-3.5" />
        </button>
      </div>
    </footer>
  );
}
