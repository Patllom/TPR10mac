'use client';

import React, { useState } from 'react';
import Image from 'next/image';
import { Layout, Code, Briefcase, Bot, Database, Cog, Layers, Sparkles, CheckCircle2, Cpu } from 'lucide-react';

export default function DigitalAISection() {
  const [activeTab, setActiveTab] = useState(0);

  const solutions = [
    {
      id: '01',
      title: 'Website Development',
      subtitle: 'Modern & High-Conversion Digital Identity',
      icon: Layout,
      description:
        'Modern responsive websites tailored to specific client requirements, brand guidelines, and strategic business goals. Built with performance-first architecture and SEO optimization.',
      highlights: [
        'Custom bespoke frontend UI/UX design',
        'Mobile-first responsive architecture',
        'SEO-optimized semantic markup and fast core web vitals',
        'Seamless CMS and content management integration',
      ],
      tag: 'WEB ARCHITECTURE',
    },
    {
      id: '02',
      title: 'Web App + AI Tools',
      subtitle: 'Smart Productivity & Interactive Dashboards',
      icon: Code,
      description:
        'Custom web applications, telemetry dashboards, REST/GraphQL APIs, and AI-enabled productivity tools designed for daily operations and decision workflows.',
      highlights: [
        'Real-time operational dashboards with WebSocket streaming',
        'Integrated AI-assisted data parsing and summarization',
        'Custom role-based access control (RBAC)',
        'Enterprise third-party API and legacy database connectors',
      ],
      tag: 'AI INTEGRATION',
    },
    {
      id: '03',
      title: 'SME System Development',
      subtitle: 'Operational Workflow & Data Optimization',
      icon: Briefcase,
      description:
        'Tailored business software systems that improve operational workflow, reduce administrative overhead, streamline inventory, and centralize data management.',
      highlights: [
        'Enterprise resource management and internal portals',
        'Automated reporting and electronic document workflows',
        'Inventory, field asset, and equipment tracking',
        'Cost-effective scalable infrastructure for growing organizations',
      ],
      tag: 'ENTERPRISE ERP',
    },
    {
      id: '04',
      title: 'Enterprise AI Platform',
      subtitle: 'Private LLMs & Intelligent Decision Support',
      icon: Bot,
      description:
        'Private and enterprise-grade AI platforms for knowledge retrieval (RAG), internal document search, automated data categorization, and executive decision support.',
      highlights: [
        'Isolated on-premise or sovereign cloud private AI deployment',
        'Retrieval-Augmented Generation (RAG) over corporate knowledge bases',
        'Automated document extraction, classification, and OCR pipelines',
        'Strict data privacy and zero external training exposure',
      ],
      tag: 'PRIVATE LLM / RAG',
    },
  ];

  const capabilities = [
    { title: 'Custom Development', icon: Code, desc: 'Tailored software engineered from ground up' },
    { title: 'Database Integration', icon: Database, desc: 'Unified relational, time-series & spatial data' },
    { title: 'Automation', icon: Cog, desc: 'Intelligent business & telemetry automation' },
    { title: 'Scalable Architecture', icon: Layers, desc: 'Cloud-native, modular and future-ready' },
  ];

  const active = solutions[activeTab];

  return (
    <section id="digital-ai" className="py-24 px-4 sm:px-6 max-w-7xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-12">
        <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono tracking-wider glass-pill text-orange-600 dark:text-cyan-400 border border-orange-500/20 mb-3">
          <Sparkles className="w-3.5 h-3.5" />
          <span>03 / DIGITAL SOLUTIONS &amp; AI</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-black tracking-tight text-slate-950 dark:text-white max-w-3xl">
          Digital Solutions &amp; AI Platforms
        </h2>
        <p className="mt-3 text-sm sm:text-base text-slate-600 dark:text-slate-300 max-w-2xl leading-relaxed">
          Custom digital platforms, intelligent applications, and private AI engines tailored for business growth and operational excellence.
        </p>
      </div>

      {/* Tab Selectors */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-8">
        {solutions.map((sol, index) => {
          const Icon = sol.icon;
          const isCurrent = activeTab === index;
          return (
            <button
              key={sol.id}
              onClick={() => setActiveTab(index)}
              className={`p-4 rounded-2xl text-left transition-all duration-300 flex flex-col justify-between border ${
                isCurrent
                  ? 'bg-gradient-to-br from-orange-500 to-amber-600 text-white shadow-xl shadow-orange-500/20 border-orange-500 scale-[1.02]'
                  : 'glass-panel text-slate-800 dark:text-slate-200 border-black/5 dark:border-white/10 hover:border-orange-500/30'
              }`}
            >
              <div className="flex items-center justify-between mb-4">
                <Icon className={`w-5 h-5 ${isCurrent ? 'text-white' : 'text-orange-500 dark:text-cyan-400'}`} />
                <span className={`text-[10px] font-mono font-bold ${isCurrent ? 'text-white/80' : 'text-slate-400'}`}>
                  {sol.id}
                </span>
              </div>
              <span className="font-bold text-xs sm:text-sm leading-tight">
                {sol.title}
              </span>
            </button>
          );
        })}
      </div>

      {/* Active Solution Detail Card with 3D Graphic Visual */}
      <div className="rounded-3xl glass-panel p-6 sm:p-10 border border-orange-500/20 shadow-2xl mb-12 bg-white/85 dark:bg-slate-950/85 overflow-hidden">
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-8 items-center">
          {/* Solution Info */}
          <div className="lg:col-span-6">
            <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-[10px] font-mono font-bold bg-orange-500/10 text-orange-600 dark:text-cyan-400 mb-3 uppercase tracking-wider">
              <Cpu className="w-3 h-3" />
              <span>{active.tag} {'//'} SOLUTION {active.id}</span>
            </div>
            <h3 className="text-2xl sm:text-3xl font-black text-slate-950 dark:text-white mb-2">
              {active.title}
            </h3>
            <p className="text-xs font-mono text-slate-500 dark:text-slate-400 mb-4">
              {active.subtitle}
            </p>
            <p className="text-sm text-slate-700 dark:text-slate-300 leading-relaxed mb-6">
              {active.description}
            </p>

            <div className="space-y-3">
              {active.highlights.map((item, i) => (
                <div key={i} className="flex items-start gap-3">
                  <CheckCircle2 className="w-4 h-4 text-orange-500 dark:text-cyan-400 flex-shrink-0 mt-0.5" />
                  <span className="text-xs sm:text-sm text-slate-800 dark:text-slate-200 font-medium">
                    {item}
                  </span>
                </div>
              ))}
            </div>
          </div>

          {/* 3D Isometric AI Graphic Showcase */}
          <div className="lg:col-span-6 relative rounded-2xl overflow-hidden shadow-2xl border border-orange-500/30 min-h-[300px] sm:min-h-[360px] flex items-end p-6 group">
            <Image
              src="/images/digital-solutions-ai.jpg"
              alt="3D Digital Solutions and Enterprise AI"
              fill
              className="object-cover transition-transform duration-700 group-hover:scale-105"
            />
            <div className="absolute inset-0 bg-gradient-to-t from-slate-950 via-slate-950/40 to-transparent" />
            <div className="relative z-10 w-full flex items-center justify-between">
              <div className="flex items-center gap-2 text-xs font-mono text-white">
                <span className="w-2 h-2 rounded-full bg-cyan-400 animate-ping" />
                <span>AI ENGINE: ONLINE</span>
              </div>
              <span className="text-[10px] font-mono font-bold text-orange-400 bg-black/60 px-2.5 py-1 rounded-lg backdrop-blur-md">
                TPR-10 INTELLIGENCE CORE
              </span>
            </div>
          </div>
        </div>
      </div>

      {/* Capabilities Strip */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {capabilities.map((cap, i) => {
          const Icon = cap.icon;
          return (
            <div key={i} className="p-4 rounded-2xl glass-panel border border-black/5 dark:border-white/10 flex items-center gap-3.5 hover:border-orange-500/30 transition-colors">
              <div className="w-10 h-10 rounded-xl bg-orange-500/10 dark:bg-cyan-500/10 flex items-center justify-center text-orange-600 dark:text-cyan-400 flex-shrink-0">
                <Icon className="w-5 h-5" />
              </div>
              <div>
                <h4 className="text-xs font-bold text-slate-900 dark:text-white">{cap.title}</h4>
                <p className="text-[11px] text-slate-500 dark:text-slate-400 leading-tight mt-0.5">{cap.desc}</p>
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}
