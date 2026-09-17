'use client';

import React from 'react';
import Image from 'next/image';
import { Network, Server, ShieldAlert, Wifi, CheckCircle2, ShieldCheck, ArrowRight, Lock } from 'lucide-react';

interface InfrastructureSectionProps {
  onOpenContact: () => void;
}

export default function InfrastructureSection({ onOpenContact }: InfrastructureSectionProps) {
  const coreServices = [
    {
      title: 'Private Network',
      description: 'Custom network topology design and robust physical/wireless deployment for industrial sites and enterprise organizations.',
      icon: Network,
      tag: 'INDUSTRIAL & ENTERPRISE',
    },
    {
      title: 'Server Installation',
      description: 'Enterprise server infrastructure, hypervisor virtualization (Proxmox/VMware), high-capacity SAN/NAS storage, and seamless system integration.',
      icon: Server,
      tag: 'INFRASTRUCTURE & STORAGE',
    },
    {
      title: 'Next-Generation Firewall',
      description: 'Advanced perimeter security policies, deep-packet threat inspection, multi-site IPSec/WireGuard VPNs, and granular zero-trust access control.',
      icon: ShieldAlert,
      tag: 'CYBERSECURITY & FIREWALL',
    },
    {
      title: 'Secure Connectivity',
      description: 'Network segmentation (VLANs/mTLS), continuous link monitoring, high-availability remote access, and verified operational stability.',
      icon: Wifi,
      tag: 'MONITORING & CONTINUITY',
    },
  ];

  const whyChooseUs = [
    {
      title: 'End-to-End Service Delivery',
      desc: 'Complete lifecycle coverage from initial site survey and architectural design to procurement, deployment, and ongoing maintenance.',
    },
    {
      title: 'Practical Solutions for Real Operations',
      desc: 'Tested and proven engineering tailored to harsh field conditions, national water telemetry, and mission-critical enterprise workloads.',
    },
    {
      title: 'Reliable Maintenance and Support',
      desc: '24/7 technical monitoring, dedicated on-site emergency field teams, and guaranteed SLA response times.',
    },
    {
      title: 'Secure, Scalable, and Future-Ready',
      desc: 'Built with open standards, zero-trust security foundations, and scalable cloud-hybrid architecture ready for next-decade growth.',
    },
  ];

  return (
    <section id="infrastructure" className="py-24 px-4 sm:px-6 max-w-7xl mx-auto">
      {/* Section Header */}
      <div className="flex flex-col items-start mb-12">
        <div className="inline-flex items-center gap-2 px-3.5 py-1.5 rounded-full text-xs font-mono tracking-wider glass-pill text-orange-600 dark:text-orange-400 border border-orange-500/20 mb-3">
          <Lock className="w-3.5 h-3.5" />
          <span>04 / INFRASTRUCTURE, NETWORK &amp; SECURITY</span>
        </div>
        <h2 className="text-3xl sm:text-4xl lg:text-5xl font-black tracking-tight text-slate-950 dark:text-white max-w-3xl">
          Infrastructure, Network &amp; Security
        </h2>
        <p className="mt-3 text-sm sm:text-base text-slate-600 dark:text-slate-300 max-w-2xl leading-relaxed">
          Secure and scalable network foundations, server deployments, and cybersecurity solutions engineered for modern organizations.
        </p>
      </div>

      {/* Featured 3D Graphic Showcase for Datacenter & Firewall */}
      <div className="relative rounded-3xl overflow-hidden glass-panel border border-orange-500/25 shadow-2xl mb-12 min-h-[300px] sm:min-h-[380px] flex items-end p-6 sm:p-10 group">
        <Image
          src="/images/infrastructure-security.jpg"
          alt="3D Enterprise Datacenter and Next-Gen Firewall"
          fill
          className="object-cover transition-transform duration-700 group-hover:scale-105"
        />
        <div className="absolute inset-0 bg-gradient-to-t from-slate-950 via-slate-950/50 to-transparent" />
        <div className="relative z-10 w-full flex flex-col sm:flex-row items-start sm:items-end justify-between gap-4">
          <div>
            <span className="inline-flex items-center gap-1.5 text-xs font-mono font-bold text-orange-400 mb-1">
              <ShieldCheck className="w-4 h-4" />
              <span>ZERO-TRUST ENTERPRISE INFRASTRUCTURE</span>
            </span>
            <h3 className="text-xl sm:text-2xl font-bold text-white">
              Industrial Server Grid &amp; Next-Gen Cybersecurity
            </h3>
          </div>
          <div className="flex items-center gap-2 px-3 py-1.5 rounded-xl bg-black/60 backdrop-blur-md border border-cyan-500/30 text-cyan-400 text-xs font-mono">
            <span className="w-2 h-2 rounded-full bg-cyan-400 animate-pulse" />
            <span>VPN MESH ENCRYPTED</span>
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-8 items-start mb-16">
        {/* Core Services 4-Card Grid (Left lg:col-span-7) */}
        <div className="lg:col-span-7 grid grid-cols-1 sm:grid-cols-2 gap-4">
          {coreServices.map((srv, idx) => {
            const Icon = srv.icon;
            return (
              <div
                key={idx}
                className="p-6 rounded-3xl glass-panel border border-orange-500/15 dark:border-white/10 hover:shadow-xl hover:shadow-orange-500/10 transition-all duration-300 flex flex-col justify-between"
              >
                <div>
                  <div className="w-10 h-10 rounded-2xl bg-orange-500/10 dark:bg-orange-500/20 text-orange-600 dark:text-orange-400 flex items-center justify-center mb-4">
                    <Icon className="w-5 h-5" />
                  </div>
                  <span className="text-[10px] font-mono font-bold text-orange-600 dark:text-orange-400 uppercase tracking-widest">
                    {srv.tag}
                  </span>
                  <h3 className="text-lg font-bold text-slate-950 dark:text-white mt-1 mb-2">
                    {srv.title}
                  </h3>
                  <p className="text-xs text-slate-600 dark:text-slate-300 leading-relaxed">
                    {srv.description}
                  </p>
                </div>
              </div>
            );
          })}
        </div>

        {/* Why TPR-10 Box (Right lg:col-span-5) */}
        <div className="lg:col-span-5 rounded-3xl glass-panel p-8 border border-orange-500/30 bg-gradient-to-br from-white/90 to-orange-50/50 dark:from-slate-950/90 dark:to-slate-900/90 shadow-2xl">
          <div className="flex items-center gap-2 mb-6 text-orange-600 dark:text-orange-400">
            <ShieldCheck className="w-6 h-6" />
            <h3 className="text-xl font-black text-slate-950 dark:text-white">
              Why TPR-10
            </h3>
          </div>

          <div className="space-y-5 mb-8">
            {whyChooseUs.map((item, index) => (
              <div key={index} className="flex items-start gap-3">
                <CheckCircle2 className="w-5 h-5 text-orange-500 flex-shrink-0 mt-0.5" />
                <div>
                  <h4 className="text-sm font-bold text-slate-950 dark:text-white">
                    {item.title}
                  </h4>
                  <p className="text-xs text-slate-600 dark:text-slate-400 leading-relaxed mt-0.5">
                    {item.desc}
                  </p>
                </div>
              </div>
            ))}
          </div>

          <button
            onClick={onOpenContact}
            className="w-full py-3.5 rounded-xl text-xs sm:text-sm font-bold bg-gradient-to-r from-orange-500 to-amber-600 hover:from-orange-600 hover:to-amber-700 text-white transition-all duration-300 shadow-lg shadow-orange-500/25 flex items-center justify-center gap-2 active:scale-98"
          >
            <span>Request Infrastructure Consultation</span>
            <ArrowRight className="w-4 h-4" />
          </button>
        </div>
      </div>
    </section>
  );
}
