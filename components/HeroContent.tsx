'use client';

import React, { useState, useEffect } from 'react';
import Image from 'next/image';
import { ArrowRight, Activity, Globe, Radio, Shield, Waves, Cpu, Lock, Wifi } from 'lucide-react';

interface HeroContentProps {
  onOpenContact: () => void;
}

export default function HeroContent({ onOpenContact }: HeroContentProps) {
  // Real-time simulated telemetry river levels and sensor data
  const [telemetryStreams, setTelemetryStreams] = useState([
    { name: 'Chao Phraya (RID-01)', level: '3.42 m', status: 'Optimal', val: 82 },
    { name: 'Ping River (RID-04)', level: '4.15 m', status: 'Normal', val: 74 },
    { name: 'Mun Basin (DWR-12)', level: '2.80 m', status: 'Optimal', val: 88 },
    { name: 'Chi River (DWR-08)', level: '3.10 m', status: 'Normal', val: 68 },
    { name: 'Pasak Dam Sensor', level: '78.5 %', status: 'Calibrated', val: 94 },
  ]);

  const [activeNodes, setActiveNodes] = useState({
    ridCentral: 12,
    dwrHub: 18,
    cloudDatacenter: 8,
    fieldGateways: 24,
  });

  useEffect(() => {
    const interval = setInterval(() => {
      setTelemetryStreams([
        { name: 'Chao Phraya (RID-01)', level: `${(3.35 + Math.random() * 0.15).toFixed(2)} m`, status: 'Optimal', val: Math.floor(78 + Math.random() * 15) },
        { name: 'Ping River (RID-04)', level: `${(4.10 + Math.random() * 0.12).toFixed(2)} m`, status: 'Normal', val: Math.floor(70 + Math.random() * 15) },
        { name: 'Mun Basin (DWR-12)', level: `${(2.75 + Math.random() * 0.18).toFixed(2)} m`, status: 'Optimal', val: Math.floor(82 + Math.random() * 14) },
        { name: 'Chi River (DWR-08)', level: `${(3.05 + Math.random() * 0.14).toFixed(2)} m`, status: 'Normal', val: Math.floor(65 + Math.random() * 20) },
        { name: 'Pasak Dam Sensor', level: `${(77.8 + Math.random() * 1.5).toFixed(1)} %`, status: 'Calibrated', val: Math.floor(90 + Math.random() * 8) },
      ]);
      setActiveNodes({
        ridCentral: Math.floor(10 + Math.random() * 4),
        dwrHub: Math.floor(16 + Math.random() * 4),
        cloudDatacenter: Math.floor(7 + Math.random() * 3),
        fieldGateways: Math.floor(22 + Math.random() * 5),
      });
    }, 2500);
    return () => clearInterval(interval);
  }, []);

  return (
    <section className="relative min-h-screen flex flex-col justify-between pt-24 pb-10 px-4 sm:px-6 max-w-7xl mx-auto z-10">
      {/* SEAMLESS ISOMETRIC BACKGROUND (Concept 2: Central Cyber Hub & VPN Core - Fully blended with 0 borders) */}
      <div className="absolute right-0 top-0 w-full lg:w-[68%] h-full pointer-events-none overflow-hidden z-0 select-none">
        {/* Radial mask to blend edges smoothly */}
        <div className="relative w-full h-full [mask-image:radial-gradient(ellipse_at_65%_48%,black_38%,transparent_78%)]">
          <Image
            src="/images/tpr10-hero-concept2-cyberhub.jpg"
            alt="TPR-10 Central Cyber Hub Background"
            fill
            priority
            className="object-cover object-center scale-105 opacity-90 dark:opacity-85 transition-opacity duration-500"
          />
        </div>

        {/* Multi-directional smooth feathering gradients */}
        <div className="absolute inset-0 bg-gradient-to-r from-[#F8FAFC] via-[#F8FAFC]/65 to-transparent dark:from-[#050811] dark:via-[#050811]/60 dark:to-transparent" />
        <div className="absolute inset-0 bg-gradient-to-t from-[#F8FAFC] via-transparent to-[#F8FAFC]/80 dark:from-[#050811] dark:via-transparent dark:to-[#050811]/70" />
        <div className="absolute inset-0 bg-gradient-to-b from-[#F8FAFC]/80 via-transparent to-transparent dark:from-[#050811]/80 dark:via-transparent dark:to-transparent" />
      </div>

      {/* Top Header & Main Layout Grid */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-8 items-start mt-2 relative z-10">
        {/* LEFT COLUMN: Main Typography & HUD Cards */}
        <div className="lg:col-span-6 flex flex-col items-start z-20">
          {/* Badge */}
          <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono tracking-wider glass-pill text-orange-600 dark:text-orange-400 border border-orange-500/20 shadow-sm mb-3">
            <span className="w-2 h-2 rounded-full bg-orange-500 animate-pulse" />
            <span className="font-bold uppercase tracking-widest text-[10px]">TPR-10 CO., LTD. // CORPORATE PROFILE</span>
          </div>

          {/* Headline */}
          <h1 className="text-3xl sm:text-5xl lg:text-6xl font-black tracking-tight text-slate-950 dark:text-white leading-[1.08] mb-3">
            Integrated IT, Network, <br />
            <span className="bg-gradient-to-r from-orange-500 via-amber-500 to-cyan-500 bg-clip-text text-transparent">
              AI &amp; Water Information
            </span>{' '}
            Solutions
          </h1>

          <p className="text-xs sm:text-sm text-slate-700 dark:text-slate-300 leading-relaxed mb-5 max-w-lg">
            Delivering end-to-end technology solutions for government agencies (RID &amp; DWR), industrial clients, and enterprise organizations. From telemetry maintenance and smart AI platforms to mission-critical infrastructure and cybersecurity.
          </p>

          {/* Action buttons */}
          <div className="flex flex-wrap items-center gap-3 mb-6">
            <button
              onClick={onOpenContact}
              className="px-5 py-2.5 rounded-xl text-xs font-bold bg-gradient-to-r from-orange-500 to-amber-600 hover:from-orange-600 hover:to-amber-700 text-white transition-all duration-300 shadow-lg shadow-orange-500/25 flex items-center gap-1.5 active:scale-98"
            >
              <span>Contact TPR-10 Specialist</span>
              <ArrowRight className="w-3.5 h-3.5" />
            </button>

            <a
              href="#telemetry"
              className="px-5 py-2.5 rounded-xl text-xs font-semibold glass-panel text-slate-800 dark:text-slate-200 hover:bg-orange-500/10 transition-all duration-200 flex items-center gap-1.5"
            >
              <Waves className="w-3.5 h-3.5 text-cyan-500" />
              <span>Explore Water Solutions</span>
            </a>
          </div>

          {/* HUD TELEMETRY CARDS STACK */}
          <div className="w-full flex flex-col gap-3">
            {/* Card 1: Telemetering & River Stream Telemetry */}
            <div className="p-3.5 rounded-2xl glass-panel border border-orange-500/20 dark:border-cyan-500/20 shadow-xl bg-white/90 dark:bg-slate-950/90 backdrop-blur-xl">
              <div className="flex items-center justify-between text-xs font-mono text-orange-600 dark:text-cyan-400 font-bold mb-2 tracking-wider">
                <span className="flex items-center gap-1.5 text-[11px]">
                  <Radio className="w-3.5 h-3.5 animate-pulse text-orange-500 dark:text-cyan-400" />
                  TELEMETRY SENSOR GRID (RID &amp; DWR)
                </span>
                <span className="text-[10px] text-emerald-600 dark:text-emerald-400 font-semibold">LIVE TRANSMISSION</span>
              </div>

              <div className="space-y-1.5">
                {telemetryStreams.slice(0, 4).map((item, idx) => (
                  <div key={idx} className="flex items-center justify-between gap-2 text-[10px] font-mono">
                    <span className="w-40 text-slate-700 dark:text-slate-300 font-medium truncate">{item.name}</span>
                    <span className="text-orange-600 dark:text-cyan-400 font-bold">{item.level}</span>
                    <div className="flex-1 max-w-[80px] h-1.5 bg-slate-200 dark:bg-slate-800 rounded-full overflow-hidden">
                      <div
                        className="h-full bg-gradient-to-r from-orange-500 to-amber-400 dark:from-cyan-500 dark:to-blue-500 rounded-full transition-all duration-700"
                        style={{ width: `${item.val}%` }}
                      />
                    </div>
                    <span className="text-[9px] px-1.5 py-0.5 rounded bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 font-semibold">
                      {item.status}
                    </span>
                  </div>
                ))}
              </div>
            </div>

            {/* Card 2: System Continuity & Active Stations */}
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              {/* Operational Reliability */}
              <div className="p-3.5 rounded-2xl glass-panel border border-orange-500/20 dark:border-cyan-500/20 shadow-xl bg-white/90 dark:bg-slate-950/90 backdrop-blur-xl">
                <div className="flex items-center justify-between text-[11px] font-mono text-orange-600 dark:text-cyan-400 font-bold mb-1 tracking-wider">
                  <span className="flex items-center gap-1">
                    <Activity className="w-3 h-3 text-orange-500" />
                    SLA CONTINUITY
                  </span>
                  <span className="text-[10px] text-emerald-600 dark:text-emerald-400 font-bold">99.99%</span>
                </div>
                <div className="h-10 w-full flex items-center justify-center">
                  <svg className="w-full h-full overflow-visible" viewBox="0 0 160 40">
                    <path
                      d="M 0 28 Q 25 12, 50 24 T 95 10 T 135 26 T 160 16"
                      fill="none"
                      stroke="#f97316"
                      strokeWidth="2.2"
                    />
                  </svg>
                </div>
              </div>

              {/* Active Infrastructure Stations */}
              <div className="p-3.5 rounded-2xl glass-panel border border-orange-500/20 dark:border-cyan-500/20 shadow-xl bg-white/90 dark:bg-slate-950/90 backdrop-blur-xl">
                <div className="text-[11px] font-mono text-orange-600 dark:text-cyan-400 font-bold mb-1 tracking-wider">
                  MISSION SITES
                </div>
                <div className="space-y-1 text-[10px] font-mono">
                  <div className="flex items-center justify-between">
                    <span className="text-slate-700 dark:text-slate-300">RID Telemetry</span>
                    <span className="text-emerald-600 dark:text-cyan-400 font-semibold">{activeNodes.ridCentral} ms (Active)</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-slate-700 dark:text-slate-300">DWR Water Hub</span>
                    <span className="text-emerald-600 dark:text-cyan-400 font-semibold">{activeNodes.dwrHub} ms (Active)</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-slate-700 dark:text-slate-300">Field Gateways</span>
                    <span className="text-emerald-600 dark:text-cyan-400 font-semibold">{activeNodes.fieldGateways} ms (Online)</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        {/* RIGHT COLUMN: Open space allowing the seamless Central Cyber Hub graphic to shine through with floating status badges */}
        <div className="hidden lg:flex lg:col-span-6 flex-col justify-between items-end h-[520px] pointer-events-none p-4">
          {/* Top Right Floating Badge */}
          <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono font-bold bg-slate-950/75 dark:bg-slate-900/80 text-cyan-400 border border-cyan-500/30 backdrop-blur-md shadow-xl animate-in fade-in slide-in-from-top-3 duration-500">
            <Lock className="w-3.5 h-3.5 text-cyan-400 animate-pulse" />
            <span>CENTRAL CYBER CORE // VPN MESH</span>
          </div>

          {/* Bottom Right Floating Badge */}
          <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono font-bold bg-slate-950/75 dark:bg-slate-900/80 text-orange-400 border border-orange-500/30 backdrop-blur-md shadow-xl">
            <Wifi className="w-3.5 h-3.5 text-orange-400 animate-pulse" />
            <span>ENTERPRISE DATA FLOW // 99.99% UPTIME</span>
          </div>
        </div>
      </div>

      {/* Bottom Floating 4-Pillar Strip (Directly matching PDF Page 1) */}
      <div id="overview" className="w-full mt-6 relative z-20">
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3 p-3.5 rounded-2xl glass-panel shadow-lg border border-orange-500/20 bg-white/85 dark:bg-slate-950/85">
          {/* Pillar 1 */}
          <div className="flex items-center gap-3 p-2">
            <div className="w-9 h-9 rounded-xl bg-orange-500/10 border border-orange-500/30 flex items-center justify-center text-orange-600 dark:text-orange-400 flex-shrink-0">
              <Waves className="w-4 h-4" />
            </div>
            <div className="flex flex-col">
              <span className="text-[10px] font-mono uppercase text-slate-500 dark:text-slate-400">Pillar 01</span>
              <span className="text-xs sm:text-sm font-bold text-slate-900 dark:text-white">Telemetry Support</span>
            </div>
          </div>

          {/* Pillar 2 */}
          <div className="flex items-center gap-3 p-2">
            <div className="w-9 h-9 rounded-xl bg-blue-500/10 border border-blue-500/30 flex items-center justify-center text-blue-600 dark:text-blue-400 flex-shrink-0">
              <Globe className="w-4 h-4" />
            </div>
            <div className="flex flex-col">
              <span className="text-[10px] font-mono uppercase text-slate-500 dark:text-slate-400">Pillar 02</span>
              <span className="text-xs sm:text-sm font-bold text-slate-900 dark:text-white">Web &amp; App Dev</span>
            </div>
          </div>

          {/* Pillar 3 */}
          <div className="flex items-center gap-3 p-2">
            <div className="w-9 h-9 rounded-xl bg-amber-500/10 border border-amber-500/30 flex items-center justify-center text-amber-600 dark:text-amber-400 flex-shrink-0">
              <Cpu className="w-4 h-4" />
            </div>
            <div className="flex flex-col">
              <span className="text-[10px] font-mono uppercase text-slate-500 dark:text-slate-400">Pillar 03</span>
              <span className="text-xs sm:text-sm font-bold text-slate-900 dark:text-white">AI Solutions</span>
            </div>
          </div>

          {/* Pillar 4 */}
          <div className="flex items-center gap-3 p-2">
            <div className="w-9 h-9 rounded-xl bg-emerald-500/10 border border-emerald-500/30 flex items-center justify-center text-emerald-600 dark:text-emerald-400 flex-shrink-0">
              <Shield className="w-4 h-4" />
            </div>
            <div className="flex flex-col">
              <span className="text-[10px] font-mono uppercase text-slate-500 dark:text-slate-400">Pillar 04</span>
              <span className="text-xs sm:text-sm font-bold text-slate-900 dark:text-white">Infra &amp; Security</span>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
