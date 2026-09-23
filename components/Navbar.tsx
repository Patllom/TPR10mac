'use client';

import React, { useState, useEffect } from 'react';
import Image from 'next/image';
import Link from 'next/link';
import { useTheme } from './ThemeContext';
import { Sun, Moon, ArrowUpRight, Menu, X } from 'lucide-react';

interface NavbarProps {
  onOpenContact: () => void;
}

export default function Navbar({ onOpenContact }: NavbarProps) {
  const { theme, toggleTheme } = useTheme();
  const [scrolled, setScrolled] = useState(false);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  useEffect(() => {
    const handleScroll = () => {
      setScrolled(window.scrollY > 20);
    };
    window.addEventListener('scroll', handleScroll);
    return () => window.removeEventListener('scroll', handleScroll);
  }, []);

  const navLinks = [
    { label: 'Overview', href: '#overview' },
    { label: 'Water & Telemetry', href: '#telemetry' },
    { label: 'Digital & AI', href: '#digital-ai' },
    { label: 'Infrastructure & Security', href: '#infrastructure' },
  ];

  return (
    <header className="fixed top-0 left-0 right-0 z-50 flex justify-center px-4 sm:px-6 pt-4 pb-2">
      <nav
        className={`w-full max-w-7xl transition-all duration-300 rounded-full px-5 py-3 flex items-center justify-between ${
          scrolled
            ? 'glass-panel shadow-lg shadow-black/5 dark:shadow-black/20'
            : 'bg-white/60 dark:bg-slate-900/60 backdrop-blur-md border border-orange-500/15 dark:border-white/10'
        }`}
      >
        {/* Brand Logo */}
        <a href="#" className="flex items-center group">
          <Image
            src="/images/tpr10-logo.png"
            alt="TPR-10 Co., Ltd."
            width={160}
            height={52}
            className="h-10 w-auto object-contain transition-opacity duration-300 group-hover:opacity-85"
            priority
          />
        </a>

        {/* Desktop Nav Links */}
        <div className="hidden lg:flex items-center gap-1">
          {navLinks.map((link) => (
            <a
              key={link.label}
              href={link.href}
              className="px-3.5 py-1.5 rounded-full text-xs font-semibold text-slate-700 dark:text-slate-300 hover:text-orange-600 dark:hover:text-orange-400 hover:bg-orange-500/10 transition-colors"
            >
              {link.label}
            </a>
          ))}
        </div>

        {/* Action Controls & Day/Night Toggle */}
        <div className="flex items-center gap-3">
          <Link href="/login" prefetch={false} className="hidden lg:inline-flex text-xs text-slate-600 dark:text-slate-300 hover:text-orange-600">
            เข้าสู่ระบบพนักงาน
          </Link>
          {/* Day / Night Theme Switch */}
          <button
            onClick={toggleTheme}
            aria-label="Toggle Day / Night Mode"
            className="relative w-14 h-7 rounded-full p-1 transition-colors duration-300 focus:outline-none focus:ring-2 focus:ring-orange-500/40 bg-slate-200/80 dark:bg-slate-800 border border-black/5 dark:border-white/10 flex items-center justify-between"
          >
            <Sun className="w-3.5 h-3.5 text-amber-500 ml-1" />
            <Moon className="w-3.5 h-3.5 text-cyan-400 mr-1" />
            <span
              className={`absolute top-0.5 w-6 h-6 rounded-full bg-white dark:bg-slate-900 shadow-md transform transition-transform duration-300 flex items-center justify-center ${
                theme === 'dark' ? 'translate-x-7' : 'translate-x-0.5'
              }`}
            >
              {theme === 'dark' ? (
                <Moon className="w-3 h-3 text-cyan-400" />
              ) : (
                <Sun className="w-3 h-3 text-amber-500" />
              )}
            </span>
          </button>

          {/* Consultation CTA Button */}
          <button
            onClick={onOpenContact}
            className="hidden sm:inline-flex items-center gap-1.5 px-4 py-2 rounded-full text-xs font-bold bg-gradient-to-r from-orange-500 to-amber-600 hover:from-orange-600 hover:to-amber-700 text-white transition-all duration-200 shadow-md shadow-orange-500/20 active:scale-95"
          >
            <span>Contact TPR-10</span>
            <ArrowUpRight className="w-3.5 h-3.5" />
          </button>

          {/* Mobile Menu Toggle Button */}
          <button
            onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
            className="lg:hidden p-1.5 rounded-lg text-slate-700 dark:text-slate-200 hover:bg-black/5 dark:hover:bg-white/10"
            aria-label="Open Navigation Menu"
          >
            {mobileMenuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
          </button>
        </div>
      </nav>

      {/* Mobile Menu Overlay */}
      {mobileMenuOpen && (
        <div className="lg:hidden fixed top-20 left-4 right-4 p-5 rounded-2xl glass-panel shadow-2xl border border-orange-500/20 flex flex-col gap-4 animate-in fade-in slide-in-from-top-4 duration-200 bg-white/95 dark:bg-slate-950/95">
          <div className="flex flex-col gap-2">
            {navLinks.map((link) => (
              <a
                key={link.label}
                href={link.href}
                onClick={() => setMobileMenuOpen(false)}
                className="px-3 py-2 rounded-lg text-sm font-semibold text-slate-800 dark:text-slate-200 hover:bg-orange-500/10 hover:text-orange-600 transition-colors"
              >
                {link.label}
              </a>
            ))}
          </div>
          <div className="pt-2 border-t border-black/5 dark:border-white/10">
            <Link href="/login" prefetch={false} onClick={() => setMobileMenuOpen(false)} className="mb-4 block px-3 py-2 text-sm text-slate-700 dark:text-slate-300">
              เข้าสู่ระบบพนักงาน
            </Link>
            <button
              onClick={() => {
                setMobileMenuOpen(false);
                onOpenContact();
              }}
              className="w-full flex items-center justify-center gap-2 py-2.5 rounded-xl text-xs font-bold bg-gradient-to-r from-orange-500 to-amber-600 text-white shadow-md"
            >
              <span>Contact TPR-10</span>
              <ArrowUpRight className="w-4 h-4" />
            </button>
          </div>
        </div>
      )}
    </header>
  );
}
