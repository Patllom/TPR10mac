'use client';

import React, { useState } from 'react';
import Navbar from '../components/Navbar';
import HeroContent from '../components/HeroContent';
import TelemetrySection from '../components/TelemetrySection';
import DigitalAISection from '../components/DigitalAISection';
import InfrastructureSection from '../components/InfrastructureSection';
import CTASection from '../components/CTASection';
import ContactModal from '../components/ContactModal';
import Footer from '../components/Footer';

export default function Home() {
  const [contactOpen, setContactOpen] = useState(false);

  return (
    <main className="min-h-screen relative flex flex-col justify-between overflow-x-hidden">
      {/* Top Floating Glass Navigation */}
      <Navbar onOpenContact={() => setContactOpen(true)} />

      {/* Hero Section with 3D Isometric Infrastructure Showcase */}
      <section className="relative w-full min-h-screen lg:h-screen lg:max-h-[960px] overflow-hidden flex flex-col justify-between">
        <HeroContent onOpenContact={() => setContactOpen(true)} />
      </section>

      {/* Page 2: Water Resources & Telemetering Support Services */}
      <TelemetrySection />

      {/* Page 3: Digital Solutions & AI Platforms */}
      <DigitalAISection />

      {/* Page 4: Infrastructure, Network & Security */}
      <InfrastructureSection onOpenContact={() => setContactOpen(true)} />

      {/* Consultation & Call to Action */}
      <CTASection onOpenContact={() => setContactOpen(true)} />

      {/* Footer with Live Operational Clocks */}
      <Footer />

      {/* Contact & Inquiry Modal */}
      <ContactModal isOpen={contactOpen} onClose={() => setContactOpen(false)} />
    </main>
  );
}
