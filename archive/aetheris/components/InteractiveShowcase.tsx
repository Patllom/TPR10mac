'use client';

import React, { useState } from 'react';
import Image from 'next/image';
import { Globe, Cpu, Layers, Activity, CheckCircle2, Copy, Check, Eye, Code2 } from 'lucide-react';

export default function InteractiveShowcase() {
  const [activeTab, setActiveTab] = useState(0);
  const [viewMode, setViewMode] = useState<'visual' | 'code'>('visual');
  const [copied, setCopied] = useState(false);

  const capabilities = [
    {
      id: 'cloud',
      label: 'Cloud Fabric',
      icon: Globe,
      title: 'Global Anycast Edge & Distributed State',
      description:
        'Eliminate cloud lock-in with intelligent multi-cloud abstraction layers. Our distributed state synchronization protocol guarantees strong eventual consistency across 30+ geographic edge zones with sub-20ms propagation.',
      imageSrc: '/images/cloud-mesh.jpg',
      imageAlt: 'Distributed Cloud Mesh Architecture',
      features: [
        'Deterministic CRDT-based multi-region state replication',
        'Automatic edge TLS termination and post-quantum handshakes',
        'Zero-egress cost intra-cluster peer mesh network',
        'Dynamic failover under partitioned network split-brain scenarios',
      ],
      metricLabel: 'State Replication Window',
      metricValue: '12.4 ms p95',
      codeTitle: 'cluster-mesh.config.ts',
      codeSnippet: `export const meshTopology = defineEdgeCluster({
  regions: ['ap-southeast-1', 'eu-central-1', 'us-east-1'],
  consistency: 'bounded-staleness',
  crdtEngine: 'state-based-pn-counter',
  failoverPolicy: {
    maxHealthProbeFailures: 2,
    rerouteStrategy: 'anycast-latency-weighted',
  },
  encryption: 'tls-1.3-post-quantum-kyber',
});`,
    },
    {
      id: 'spatial',
      label: 'Spatial 3D & UI',
      icon: Layers,
      title: 'Hardware-Accelerated WebGL & Spatial Interfaces',
      description:
        'Translating multi-dimensional complex systems into effortless, intuitive digital canvases. Custom shader pipelines, progressive asset streaming, and 60 FPS silky smooth performance across desktop and mobile.',
      imageSrc: '/images/spatial-engine.jpg',
      imageAlt: 'Spatial 3D Innovation Lab',
      features: [
        'GPU-instanced rendering pipelines with automated level-of-detail',
        'Real-time PBR material lighting synchronized to atmospheric ambient data',
        'Zero-layout-shift hybrid server/client component hydration',
        'Strict WCAG 2.1 AAA accessibility and reduced-motion fallbacks',
      ],
      metricLabel: 'Client Render Budget',
      metricValue: '60 FPS @ 4K',
      codeTitle: 'renderPipeline.ts',
      codeSnippet: `const spatialShader = new THREE.ShaderMaterial({
  uniforms: {
    uTime: { value: 0 },
    uThemeLight: { value: theme === 'light' ? 1.0 : 0.0 },
    uRoughness: { value: 0.12 },
    uIridescence: { value: 0.85 },
  },
  vertexShader: atmosphericVertexShader,
  fragmentShader: chromaticDispersionFragment,
  transparent: true,
});`,
    },
    {
      id: 'architecture',
      label: 'Kinetic Systems',
      icon: Cpu,
      title: 'Parametric Engineering & Architectural Rigor',
      description:
        'Unifying physical architectural aesthetics with computational fluid dynamics. Every system is built as a kinetic digital sculpture that adapts to traffic demands and real-time enterprise telemetry.',
      imageSrc: '/images/hero-day.jpg',
      imageAlt: 'Kinetic Architecture and Digital Ribbon',
      features: [
        'Parametric mathematical modeling for resilient system load distribution',
        'Zero-downtime hot reloading of core computational modules',
        'Full formal verification of transaction invariant state transitions',
        'Hardware-accelerated cryptographic security enclaves',
      ],
      metricLabel: 'Deterministic Throughput',
      metricValue: '850K+ ops/sec',
      codeTitle: 'kinetic_engine.rs',
      codeSnippet: `pub struct KineticEngine<const CAPACITY: usize> {
    order_book: RingBuffer<Order, CAPACITY>,
    state_tree: MerklePatriciaTree,
    journal: DirectIoWal,
}

impl<const C: usize> KineticEngine<C> {
    #[inline(always)]
    pub fn process_event(&mut self, event: Event) -> Result<ExecutionReport, EngineError> {
        self.journal.append_sync(&event)?;
        self.order_book.push_lockfree(event.to_order())
    }
}`,
    },
    {
      id: 'mesh',
      label: 'Telemetry Mesh',
      icon: Activity,
      title: 'Autonomous Observability & Self-Healing',
      description:
        'Continuous deep-packet inspection via eBPF probes. Detect anomalies, memory leaks, and distributed deadlocks before they manifest into user-facing incidents with automated remediation playbooks.',
      imageSrc: '/images/cloud-mesh.jpg',
      imageAlt: 'Autonomous Observability and Telemetry Probe',
      features: [
        'Kernel-level eBPF distributed telemetry with zero user-space overhead',
        'Predictive auto-scaling based on upstream queue pressure gradients',
        'Automatic microservice trace correlation and root-cause clustering',
        'Autonomous circuit breakers with simulated chaos injection verification',
      ],
      metricLabel: 'Mean Time to Mitigation (MTTM)',
      metricValue: '< 4.2 seconds',
      codeTitle: 'telemetry_probe.yaml',
      codeSnippet: `apiVersion: aetheris.io/v1alpha1
kind: AutonomousProbe
metadata:
  name: edge-traffic-anomaly-guard
spec:
  ebpfHook: kprobe:tcp_sendmsg
  mitigation:
    action: dynamic-rate-throttle
    thresholdRps: 45000
    alertChannel: security-sre-ops`,
    },
  ];

  const current = capabilities[activeTab];

  const handleCopy = () => {
    navigator.clipboard.writeText(current.codeSnippet);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <section id="showcase" className="py-24 px-4 sm:px-6 max-w-6xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-14">
        <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-xs font-mono tracking-wider glass-pill text-amber-800 dark:text-cyan-400 mb-4">
          <span>02 / CAPABILITIES &amp; BLUEPRINTS</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-bold tracking-tight text-slate-950 dark:text-white max-w-2xl">
          Deep engineering capabilities, designed with architectural excellence.
        </h2>
      </div>

      {/* Tab Selectors */}
      <div className="flex flex-wrap gap-2 sm:gap-3 p-1.5 rounded-2xl glass-panel mb-8">
        {capabilities.map((item, index) => {
          const Icon = item.icon;
          const isActive = activeTab === index;
          return (
            <button
              key={item.id}
              onClick={() => setActiveTab(index)}
              className={`flex-1 min-w-[140px] flex items-center justify-center gap-2.5 py-3 px-4 rounded-xl text-xs sm:text-sm font-semibold transition-all duration-200 ${
                isActive
                  ? 'bg-gradient-to-r from-amber-600 to-slate-900 dark:from-white dark:to-slate-100 text-white dark:text-slate-950 shadow-md scale-100'
                  : 'text-slate-700 dark:text-slate-400 hover:text-slate-950 dark:hover:text-white hover:bg-amber-500/10 dark:hover:bg-white/5'
              }`}
            >
              <Icon className="w-4 h-4" />
              <span>{item.label}</span>
            </button>
          );
        })}
      </div>

      {/* Content Showcase Card */}
      <div className="rounded-3xl glass-panel p-6 sm:p-10 grid grid-cols-1 lg:grid-cols-12 gap-8 items-center border border-amber-500/15 dark:border-white/10 shadow-2xl">
        {/* Left Column: Narrative & Features */}
        <div className="lg:col-span-6 flex flex-col justify-between h-full">
          <div>
            <span className="text-xs font-mono uppercase tracking-widest text-amber-700 dark:text-cyan-400 font-bold">
              SYSTEM BLUEPRINT // {current.id.toUpperCase()}
            </span>
            <h3 className="text-2xl sm:text-3xl font-bold text-slate-950 dark:text-white mt-2 mb-4">
              {current.title}
            </h3>
            <p className="text-sm sm:text-base text-slate-700 dark:text-slate-300 leading-relaxed mb-6">
              {current.description}
            </p>

            {/* Feature Checklist */}
            <div className="space-y-3 mb-8">
              {current.features.map((feat, i) => (
                <div key={i} className="flex items-start gap-3">
                  <CheckCircle2 className="w-4 h-4 text-amber-600 dark:text-emerald-400 flex-shrink-0 mt-0.5" />
                  <span className="text-xs sm:text-sm text-slate-800 dark:text-slate-200 font-medium">
                    {feat}
                  </span>
                </div>
              ))}
            </div>
          </div>

          {/* Metric Highlight Badge */}
          <div className="pt-6 border-t border-amber-500/15 dark:border-white/10 flex items-center justify-between">
            <span className="text-xs font-mono text-slate-600 dark:text-slate-400">
              {current.metricLabel}
            </span>
            <span className="text-lg font-bold font-mono px-3 py-1 rounded-lg bg-amber-500/10 dark:bg-cyan-400/10 text-amber-800 dark:text-cyan-300 border border-amber-500/20 dark:border-cyan-400/20">
              {current.metricValue}
            </span>
          </div>
        </div>

        {/* Right Column: Interactive Visual / Code Switcher */}
        <div className="lg:col-span-6 rounded-2xl overflow-hidden shadow-2xl border border-black/10 dark:border-slate-800 flex flex-col bg-slate-950">
          {/* Card View Mode Toggle Header */}
          <div className="flex items-center justify-between px-4 py-3 bg-slate-900 border-b border-slate-800 text-xs font-mono">
            <div className="flex items-center gap-2">
              <button
                onClick={() => setViewMode('visual')}
                className={`flex items-center gap-1.5 px-2.5 py-1 rounded-lg transition-colors ${
                  viewMode === 'visual'
                    ? 'bg-amber-500 text-slate-950 font-bold'
                    : 'text-slate-400 hover:text-white'
                }`}
              >
                <Eye className="w-3.5 h-3.5" />
                <span>Visual Mockup</span>
              </button>
              <button
                onClick={() => setViewMode('code')}
                className={`flex items-center gap-1.5 px-2.5 py-1 rounded-lg transition-colors ${
                  viewMode === 'code'
                    ? 'bg-amber-500 text-slate-950 font-bold'
                    : 'text-slate-400 hover:text-white'
                }`}
              >
                <Code2 className="w-3.5 h-3.5" />
                <span>Source Engine</span>
              </button>
            </div>

            {viewMode === 'code' && (
              <button
                onClick={handleCopy}
                className="flex items-center gap-1 text-[11px] text-slate-400 hover:text-white transition-colors"
              >
                {copied ? (
                  <>
                    <Check className="w-3.5 h-3.5 text-emerald-400" />
                    <span className="text-emerald-400">Copied</span>
                  </>
                ) : (
                  <>
                    <Copy className="w-3.5 h-3.5" />
                    <span>Copy</span>
                  </>
                )}
              </button>
            )}
          </div>

          {/* Visual Display or Code Preview */}
          {viewMode === 'visual' ? (
            <div className="relative w-full h-[320px] sm:h-[360px] overflow-hidden group">
              <Image
                src={current.imageSrc}
                alt={current.imageAlt}
                fill
                className="object-cover transition-transform duration-700 group-hover:scale-105"
                sizes="(max-width: 768px) 100vw, 50vw"
                priority
              />
              <div className="absolute inset-0 bg-gradient-to-t from-slate-950/90 via-slate-950/20 to-transparent flex flex-col justify-end p-6">
                <span className="text-xs font-mono text-amber-400 font-bold tracking-widest uppercase">
                  AETHERIS ARCHITECTURAL ENVIRONMENT
                </span>
                <span className="text-sm font-semibold text-white mt-0.5">
                  {current.imageAlt}
                </span>
              </div>
            </div>
          ) : (
            <div className="flex flex-col">
              <pre className="p-5 text-xs sm:text-[13px] font-mono text-slate-200 overflow-x-auto leading-relaxed h-[280px] sm:h-[320px]">
                <code>{current.codeSnippet}</code>
              </pre>
              <div className="px-4 py-2 bg-slate-900/90 border-t border-slate-800 flex items-center justify-between text-[11px] font-mono text-slate-400">
                <span className="flex items-center gap-2">
                  <span className="w-1.5 h-1.5 rounded-full bg-emerald-400" />
                  SYNCHRONIZED // UTF-8
                </span>
                <span>AETHERIS COMPILER v4.2</span>
              </div>
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
