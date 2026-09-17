'use client';

import React from 'react';
import Image from 'next/image';
import { Radio, Wrench, PackageCheck, Headphones, ShieldCheck, Zap, RefreshCw, Droplet, Activity, MapPin } from 'lucide-react';

export default function TelemetrySection() {
  const projects = [
    {
      number: '01',
      title: 'RID and DWR Telemetering Project',
      description:
        'Design, engineering, and turn-key installation of automated telemetering stations and sensor systems for the Royal Irrigation Department (กรมชลประทาน) and the Department of Water Resources (กรมทรัพยากรน้ำ).',
      icon: Radio,
      badge: 'Design & Installation',
      stat: '500+ Stations Deployed',
    },
    {
      number: '02',
      title: 'RID and DWR Maintenance Telemetering',
      description:
        'Comprehensive 24/7 system operation support, preventive and corrective maintenance, remote real-time monitoring, troubleshooting, and continuous data availability.',
      icon: Wrench,
      badge: 'Operation & Maintenance',
      stat: '24/7 Dispatch SLA',
    },
    {
      number: '03',
      title: 'RID and DWR Spare Part Project',
      description:
        'Strategic spare parts planning, inventory management, rapid component replacement, and full lifecycle support for critical telemetric sensors and telemetry equipment.',
      icon: PackageCheck,
      badge: 'Spare Parts & Lifecycle',
      stat: '100% Critical Reserves',
    },
    {
      number: '04',
      title: 'Field & Data Support Services',
      description:
        'On-site technical support for field sensor devices, cellular/satellite communications, real-time river dashboard integration, and verified data availability.',
      icon: Headphones,
      badge: 'Field Operations',
      stat: '99.99% Stream Uptime',
    },
  ];

  const keyValues = [
    { title: 'Operational Reliability', desc: 'Proven uptime across national water basin stations', icon: ShieldCheck },
    { title: 'Fast Response', desc: 'Rapid on-site emergency dispatch and troubleshooting SLA', icon: Zap },
    { title: 'System Continuity', desc: 'Fault-tolerant telemetry data streams without interruption', icon: RefreshCw },
    { title: 'Water Management Support', desc: 'Empowering national flood warning and drought planning', icon: Droplet },
  ];

  return (
    <section id="telemetry" className="py-24 px-4 sm:px-6 max-w-7xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-12">
        <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono tracking-wider glass-pill text-orange-600 dark:text-orange-400 border border-orange-500/20 mb-3">
          <Droplet className="w-3.5 h-3.5" />
          <span>02 / WATER RESOURCES &amp; TELEMETERING</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-black tracking-tight text-slate-950 dark:text-white max-w-3xl">
          Water Resources &amp; Telemetering Support Services
        </h2>
        <p className="mt-3 text-sm sm:text-base text-slate-600 dark:text-slate-300 max-w-2xl leading-relaxed">
          Reliable engineering and maintenance support for national telemetry operations, hydrological monitoring stations, and water information systems.
        </p>
      </div>

      {/* Featured 3D Graphic Showcase Grid */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 mb-12">
        {/* Main Graphic Banner: Water Dam & Hydrological Telemetry */}
        <div className="lg:col-span-7 relative rounded-3xl overflow-hidden glass-panel border border-orange-500/25 shadow-2xl group min-h-[340px] sm:min-h-[400px] flex flex-col justify-end p-6 sm:p-8">
          <Image
            src="/images/water-telemetry-dam.jpg"
            alt="3D Water Dam and Telemetry Station"
            fill
            className="object-cover transition-transform duration-700 group-hover:scale-105"
          />
          {/* Gradient Overlay */}
          <div className="absolute inset-0 bg-gradient-to-t from-slate-950 via-slate-950/60 to-transparent" />

          {/* Floating Telemetry Badges */}
          <div className="relative z-10">
            <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-[11px] font-mono tracking-wider bg-orange-500/90 text-white font-bold mb-3 shadow-lg">
              <Activity className="w-3 h-3 animate-pulse" />
              <span>LIVE HYDROLOGICAL TELEMETRY // RID &amp; DWR</span>
            </div>
            <h3 className="text-xl sm:text-2xl font-bold text-white mb-2">
              National Water Basin Monitoring Grid
            </h3>
            <p className="text-xs sm:text-sm text-slate-200 leading-relaxed max-w-xl">
              Equipped with radar water level sensors, automated rain gauges, solar backup power, and satellite/cellular telemetry dispatch.
            </p>
          </div>
        </div>

        {/* Secondary Graphic Banner: Field Support & Spare Parts Logistics */}
        <div className="lg:col-span-5 relative rounded-3xl overflow-hidden glass-panel border border-orange-500/25 shadow-2xl group min-h-[340px] sm:min-h-[400px] flex flex-col justify-end p-6 sm:p-8">
          <Image
            src="/images/field-telemetry-ops.jpg"
            alt="3D Field Telemetry Operations and Spare Parts Support"
            fill
            className="object-cover transition-transform duration-700 group-hover:scale-105"
          />
          {/* Gradient Overlay */}
          <div className="absolute inset-0 bg-gradient-to-t from-slate-950 via-slate-950/60 to-transparent" />

          <div className="relative z-10">
            <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-[11px] font-mono tracking-wider bg-cyan-600/90 text-white font-bold mb-3 shadow-lg">
              <MapPin className="w-3 h-3" />
              <span>24/7 FIELD CREW &amp; SPARE PARTS</span>
            </div>
            <h3 className="text-xl sm:text-2xl font-bold text-white mb-2">
              On-Site Maintenance Logistics
            </h3>
            <p className="text-xs sm:text-sm text-slate-200 leading-relaxed">
              Rapid on-site emergency repair, sensor calibration, and full inventory management for mission-critical water telemetry.
            </p>
          </div>
        </div>
      </div>

      {/* 4 Projects Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mb-12">
        {projects.map((proj) => {
          const Icon = proj.icon;
          return (
            <div
              key={proj.number}
              className="p-7 sm:p-8 rounded-3xl glass-panel border border-orange-500/15 dark:border-white/10 transition-all duration-300 hover:shadow-xl hover:shadow-orange-500/10 hover:-translate-y-1 flex flex-col justify-between"
            >
              <div>
                <div className="flex items-center justify-between mb-6">
                  <div className="w-12 h-12 rounded-2xl bg-orange-500/10 dark:bg-orange-500/20 border border-orange-500/30 flex items-center justify-center text-orange-600 dark:text-orange-400">
                    <Icon className="w-6 h-6" />
                  </div>
                  <span className="font-mono text-xs font-bold text-orange-600/80 dark:text-orange-400/80 px-2.5 py-1 rounded-full bg-orange-500/10">
                    {proj.stat}
                  </span>
                </div>

                <span className="text-[11px] font-mono uppercase tracking-wider text-orange-600 dark:text-orange-400 font-bold">
                  {proj.badge}
                </span>
                <h3 className="text-xl sm:text-2xl font-bold text-slate-950 dark:text-white mt-1 mb-3">
                  {proj.title}
                </h3>
                <p className="text-xs sm:text-sm text-slate-600 dark:text-slate-300 leading-relaxed">
                  {proj.description}
                </p>
              </div>
            </div>
          );
        })}
      </div>

      {/* Key Values Strip */}
      <div className="rounded-3xl glass-panel p-6 sm:p-8 border border-orange-500/20 bg-white/80 dark:bg-slate-950/80 shadow-xl">
        <div className="text-xs font-mono uppercase tracking-widest text-orange-600 dark:text-orange-400 font-bold mb-6 flex items-center gap-2">
          <span className="w-2 h-2 rounded-full bg-orange-500" />
          KEY VALUE COMMITMENTS // TPR-10 STANDARD
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6">
          {keyValues.map((kv, idx) => {
            const Icon = kv.icon;
            return (
              <div key={idx} className="flex flex-col gap-2 p-4 rounded-2xl bg-black/5 dark:bg-white/5 border border-black/5 dark:border-white/5">
                <div className="flex items-center gap-2 text-slate-950 dark:text-white font-bold text-sm">
                  <Icon className="w-4 h-4 text-orange-500 flex-shrink-0" />
                  <span>{kv.title}</span>
                </div>
                <p className="text-xs text-slate-600 dark:text-slate-400 leading-relaxed">
                  {kv.desc}
                </p>
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}
