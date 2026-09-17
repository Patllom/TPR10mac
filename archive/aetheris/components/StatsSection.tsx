'use client';

import React from 'react';
import { ShieldCheck, Zap, TrendingUp, Globe2 } from 'lucide-react';

export default function StatsSection() {
  const stats = [
    {
      value: '99.999%',
      label: 'Fault-Tolerant SLA',
      description: 'Continuous uptime across multi-region active clusters with zero unplanned outages.',
      icon: ShieldCheck,
    },
    {
      value: '< 24ms',
      label: 'Edge Latency (p99)',
      description: 'Deterministic packet routing through 32 Anycast global edge locations.',
      icon: Zap,
    },
    {
      value: '$4.8B+',
      label: 'Daily Asset Throughput',
      description: 'Secured transaction volume handled through our core high-frequency engines.',
      icon: TrendingUp,
    },
    {
      value: '140+',
      label: 'Enterprise Deployments',
      description: 'Trusted by tier-one financial institutions, sovereign entities, and deep-tech unicorns.',
      icon: Globe2,
    },
  ];

  const trustedSectors = [
    'GLOBAL FINANCIAL EXCHANGES',
    'AUTONOMOUS MOBILITY NETWORKS',
    'HIGH-THROUGHPUT ENERGY TRADING',
    'SOVEREIGN CLOUD INFRASTRUCTURE',
    'BIOMEDICAL COMPUTE CLUSTERS',
  ];

  return (
    <section id="metrics" className="py-24 px-4 sm:px-6 max-w-6xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-16">
        <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-xs font-mono tracking-wider glass-pill text-blue-600 dark:text-cyan-400 mb-4">
          <span>03 / PERFORMANCE METRICS</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-bold tracking-tight text-slate-950 dark:text-white max-w-2xl">
          Empirical proof behind mathematical systems.
        </h2>
      </div>

      {/* Metrics Grid */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6 mb-16">
        {stats.map((stat, i) => {
          const Icon = stat.icon;
          return (
            <div
              key={i}
              className="p-8 rounded-3xl glass-panel transition-all duration-300 hover:shadow-xl hover:shadow-black/5 dark:hover:shadow-blue-500/5 hover:-translate-y-1 flex flex-col justify-between"
            >
              <div>
                <div className="w-10 h-10 rounded-xl bg-slate-100 dark:bg-slate-800 border border-black/5 dark:border-white/10 flex items-center justify-center text-slate-900 dark:text-white mb-6">
                  <Icon className="w-5 h-5 text-blue-600 dark:text-cyan-400" />
                </div>
                <div className="text-3xl sm:text-4xl font-bold font-mono text-slate-950 dark:text-white tracking-tight mb-2">
                  {stat.value}
                </div>
                <div className="text-sm font-semibold text-slate-900 dark:text-slate-200 mb-3">
                  {stat.label}
                </div>
                <p className="text-xs text-slate-500 dark:text-slate-400 leading-relaxed">
                  {stat.description}
                </p>
              </div>
            </div>
          );
        })}
      </div>

      {/* Trusted Sectors Marquee Bar */}
      <div className="rounded-2xl glass-panel p-6 border border-black/5 dark:border-white/10">
        <div className="text-center text-xs font-mono uppercase tracking-[0.25em] text-slate-400 dark:text-slate-500 mb-4">
          POWERING MISSION-CRITICAL INFRASTRUCTURE ACROSS
        </div>
        <div className="flex flex-wrap items-center justify-center gap-4 sm:gap-8 text-xs font-mono font-semibold text-slate-700 dark:text-slate-300">
          {trustedSectors.map((sector, idx) => (
            <div key={idx} className="flex items-center gap-3">
              <span className="w-1.5 h-1.5 rounded-full bg-blue-500/60 dark:bg-cyan-400/60" />
              <span>{sector}</span>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
