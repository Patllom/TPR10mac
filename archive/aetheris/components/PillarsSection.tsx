'use client';

import React from 'react';
import { Network, Zap, Sparkles, Lock, ArrowUpRight } from 'lucide-react';

export default function PillarsSection() {
  const pillars = [
    {
      id: '01',
      title: 'Distributed Cloud Architecture',
      subtitle: 'Fault-Tolerant & Multi-Region',
      description:
        'Resilient distributed systems engineered to eliminate single points of failure. Designed with automated zero-downtime rollouts, intelligent traffic routing, and sovereign cloud failover.',
      icon: Network,
      tags: ['Kubernetes', 'gRPC', 'Multi-Region Mesh', 'eBPF'],
      colSpan: 'md:col-span-2 lg:col-span-2',
      accent: 'amber',
    },
    {
      id: '02',
      title: 'Real-Time Streaming Engines',
      subtitle: 'Sub-Millisecond Latency',
      description:
        'High-throughput analytical pipelines and event-driven backbones processing millions of concurrent transactions with deterministic execution.',
      icon: Zap,
      tags: ['Kafka', 'ClickHouse', 'Rust Kernels', 'Memory-Mapped I/O'],
      colSpan: 'md:col-span-1 lg:col-span-1',
      accent: 'emerald',
    },
    {
      id: '03',
      title: 'Kinetic Digital Interfaces',
      subtitle: 'Spatial Web Experiences',
      description:
        'Immersive WebGL 3D, physics-based motion design, and tactile web surfaces that convert complex enterprise data into frictionless human interactions.',
      icon: Sparkles,
      tags: ['Three.js', 'WebGPU', 'Parametric UI', 'RSC Architecture'],
      colSpan: 'md:col-span-1 lg:col-span-1',
      accent: 'blue',
    },
    {
      id: '04',
      title: 'Zero-Trust Security & Sovereignty',
      subtitle: 'Cryptographic Integrity',
      description:
        'Military-grade cryptographic protocols, hardware security module enclaves, and continuous architectural auditing to guarantee enterprise data sovereignty.',
      icon: Lock,
      tags: ['Post-Quantum Kyber', 'mTLS', 'SOC2 / ISO 27001', 'Confidential Compute'],
      colSpan: 'md:col-span-2 lg:col-span-2',
      accent: 'amber',
    },
  ];

  return (
    <section id="pillars" className="py-24 px-4 sm:px-6 max-w-6xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-16">
        <div className="inline-flex items-center gap-2 px-3.5 py-1 rounded-full text-xs font-mono tracking-wider glass-pill text-amber-800 dark:text-cyan-400 mb-4">
          <span>01 / CORE ARCHITECTURAL PILLARS</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-bold tracking-tight text-slate-950 dark:text-white max-w-2xl">
          Architectural principles engineered for perpetual scale.
        </h2>
        <p className="mt-4 text-base text-slate-700 dark:text-slate-300 max-w-2xl leading-relaxed">
          We do not build disposable software. Every layer of our technology stack is designed to withstand
          decades of evolving enterprise demands, compliance shifts, and traffic spikes.
        </p>
      </div>

      {/* Bento Grid */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
        {pillars.map((pillar) => {
          const Icon = pillar.icon;
          return (
            <div
              key={pillar.id}
              className={`relative group rounded-3xl p-8 glass-panel transition-all duration-300 hover:shadow-2xl hover:shadow-amber-500/10 dark:hover:shadow-blue-500/10 hover:-translate-y-1.5 flex flex-col justify-between border border-amber-500/15 dark:border-white/10 ${pillar.colSpan}`}
            >
              {/* Card Header */}
              <div>
                <div className="flex items-center justify-between mb-6">
                  <div className="w-12 h-12 rounded-2xl bg-amber-500/10 dark:bg-slate-800 border border-amber-500/20 dark:border-white/10 flex items-center justify-center text-amber-700 dark:text-cyan-300 transition-colors duration-300 group-hover:bg-gradient-to-r group-hover:from-amber-600 group-hover:to-slate-900 group-hover:text-white">
                    <Icon className="w-5 h-5" />
                  </div>
                  <span className="font-mono text-xs font-bold text-amber-800/60 dark:text-slate-500">
                    {pillar.id}
                  </span>
                </div>

                <div className="mb-2">
                  <span className="text-xs font-mono uppercase tracking-wider text-amber-700 dark:text-cyan-400 font-bold">
                    {pillar.subtitle}
                  </span>
                  <h3 className="text-xl sm:text-2xl font-bold text-slate-950 dark:text-white mt-1">
                    {pillar.title}
                  </h3>
                </div>

                <p className="text-sm text-slate-700 dark:text-slate-300 leading-relaxed mt-4">
                  {pillar.description}
                </p>
              </div>

              {/* Card Footer Tech Tags */}
              <div className="mt-8 pt-6 border-t border-amber-500/15 dark:border-white/10 flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap gap-1.5">
                  {pillar.tags.map((tag) => (
                    <span
                      key={tag}
                      className="px-2.5 py-1 rounded-md text-[11px] font-mono bg-amber-500/5 dark:bg-white/5 text-slate-800 dark:text-slate-300 border border-amber-500/15 dark:border-white/5"
                    >
                      {tag}
                    </span>
                  ))}
                </div>

                <div className="w-8 h-8 rounded-full flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity duration-200 text-slate-950 dark:text-white">
                  <ArrowUpRight className="w-4 h-4" />
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}
