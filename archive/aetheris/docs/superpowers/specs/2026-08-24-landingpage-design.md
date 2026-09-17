# Modern & Premium Corporate Landing Page Design Specification

## Overview
- **Project**: AETHERIS - Modern Digital Architecture & Enterprise Technology Consulting
- **Goal**: Create a high-end, bespoke corporate landing page with a full-viewport 3D kinetic sculpture hero section in Three.js and real-time Day/Night theme toggling.
- **Tone & Style**: Editorial minimalism, intentional typography, clean monochrome palette with precision metallic accents, zero generic AI clichés.

## Technology Stack
- **Framework**: Next.js 14 (App Router)
- **Language**: TypeScript
- **Styling**: Tailwind CSS (with custom design token extensions)
- **3D Graphics**: Three.js (r128+ via direct canvas lifecycle hook)
- **Motion & Icons**: Framer Motion, Lucide React

## Visual & Token System

### Color Palette
- **Light Theme**:
  - `bg-primary`: `#F8F9FA` (Soft Platinum)
  - `surface`: `#FFFFFF` with `rgba(0,0,0,0.06)` border
  - `text-primary`: `#0D1117`
  - `text-secondary`: `#555D6E`
  - `accent`: `#2563EB` (Cobalt) & `#0F172A` (Obsidian)
  - `3D Material`: Titanium / White Ceramic / Polished Chrome with subtle gold sheen
- **Dark Theme**:
  - `bg-primary`: `#08090C` (Void Onyx)
  - `surface`: `#11141C` with `rgba(255,255,255,0.08)` border
  - `text-primary`: `#F9FAFB`
  - `text-secondary`: `#94A3B8`
  - `accent`: `#38BDF8` (Cyan Blue) & `#818CF8` (Indigo)
  - `3D Material`: Dark Obsidian Glass / Iridescent Core / Luminous Rim Lighting

### Typography
- Primary Sans: Inter / Plus Jakarta Sans system stack
- Monospace / Metrics: JetBrains Mono / Space Grotesk styling

## Core Architecture & Components

### 1. Theme Management (`components/ThemeContext.tsx`)
- Provides `theme` ('light' | 'dark') and `toggleTheme()` via React Context.
- Persists user choice in `localStorage` and syncs with HTML `dark` class.
- Emits events / state updates to Three.js canvas for synchronized WebGL material transition.

### 2. Three.js 3D Hero Canvas (`components/Hero3D.tsx`)
- **Container**: Absolute `w-full h-full inset-0 pointer-events-none` with canvas handling pointer events or window mouse listeners.
- **Scene Geometry**:
  - Central Polyhedron: `IcosahedronGeometry(1.8, 0)` with faceted physical material.
  - Outer Orbital Ring 1: `TorusGeometry(2.6, 0.035, 16, 100)` angled at 35°.
  - Outer Orbital Ring 2: `TorusGeometry(3.2, 0.02, 16, 100)` angled at -45°.
  - Particle Field: `BufferGeometry` with 350 floating points with subtle drift.
- **Physics / Interaction**:
  - Target rotation interpolated via Lerp (`smoothStep`) with mouse coordinates.
  - Ambient slow rotation on all axes.
  - Lighting morphing: Dynamic color, intensity, and fog transition on theme change.
- **Performance**:
  - ResizeObserver for DPR scaling (capped at 2 for retina performance).
  - Clean disposal of geometries, materials, and requestAnimationFrame on unmount.

### 3. Navigation Bar (`components/Navbar.tsx`)
- Floating pill / glassmorphism navbar.
- Brand logo: Minimal geometric monogram + wordmark "AETHERIS".
- Links: Architecture, Capabilities, Insights, Company.
- Day/Night switch: Smooth sliding toggle button with Sun/Moon icons.
- CTA Button: "Consult with Us".

### 4. Hero Content (`components/HeroContent.tsx`)
- Status pill: "● NEXT-GENERATION ENTERPRISE ARCHITECTURE"
- Headline: "Engineering the Structural Future of Digital Systems."
- Subtitle: "We partner with visionary enterprises to architect resilient digital platforms, bespoke software engines, and immersive interfaces."
- Dual CTA: "Explore Capabilities" (Primary) & "View Case Studies" (Secondary Ghost).
- Metric Strip: Real-time latency, architecture benchmark, client retention.

### 5. Strategic Pillars (`components/PillarsSection.tsx`)
- 4-Card Bento Grid:
  - 01. Autonomous Systems & High-Throughput Engines
  - 02. Precision Cloud & Resilient Infrastructure
  - 03. Bespoke Digital Interface Engineering
  - 04. Strategic Intelligence & Architectural Governance

### 6. Interactive Showcase (`components/InteractiveShowcase.tsx`)
- Multi-tab or interactive card stack detailing specialized solutions.
- Micro-interactions on hover with smooth glow borders.

### 7. Global Metrics & Trust (`components/StatsSection.tsx`)
- 4 Key Metrics: 99.999% Reliability, 40ms Global Latency, $4.2B+ Transaction Volume Handled, 140+ Enterprise Deployments.

### 8. Contact & Consultation (`components/CTASection.tsx` & `components/ContactModal.tsx`)
- Direct consultation scheduler & interactive contact form.

### 9. Footer (`components/Footer.tsx`)
- Live timezone indicators (Bangkok UTC+7, Tokyo UTC+9, London UTC+0, New York UTC-5).
- Legal, navigation, and copyright.

## Verification & Acceptance Criteria
- 60 FPS WebGL rendering without memory leaks.
- Seamless, instant light/dark mode transitions without flash of unstyled content.
- Fully responsive on mobile, tablet, and widescreen desktop displays.
- Zero TypeScript errors and clean production build.
