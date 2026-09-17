# Modern & Premium Corporate Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a bespoke, modern & premium corporate landing page for "AETHERIS" with a full-viewport interactive Three.js 3D kinetic sculpture, seamless Day/Night theme toggling, and clean architectural minimalism using Next.js 14, Tailwind CSS, TypeScript, and Framer Motion.

**Architecture:** Next.js 14 App Router structure. High-performance isolated Three.js canvas in `components/Hero3D.tsx` synchronizing lighting/materials dynamically with `ThemeContext`. Clean decoupled UI components with frosted glass styling, micro-interactions, and live time-zone indicators.

**Tech Stack:** Next.js 14 (App Router), React 18, TypeScript, Tailwind CSS, Three.js, Lucide React, Framer Motion, clsx, tailwind-merge.

**Spec:** `docs/superpowers/specs/2026-08-24-landingpage-design.md`

## Global Constraints
- Node.js 20+ runtime located at `$HOME/.nodejs/bin`
- Project root: `/Users/theerapat_k/Documents/landingpage `
- All styling must strictly honor Light / Dark theme tokens without CSS flickering
- WebGL canvas must cleanly dispose geometries/materials on unmount and achieve 60 FPS
- No generic AI-template looks: clean typography, deliberate whitespace, refined micro-interactions

---

### Task 1: Next.js Project Scaffolding & Dependency Setup

**Files:**
- Create: `package.json`, `tsconfig.json`, `tailwind.config.js`, `postcss.config.js`, `next.config.js`
- Create: `app/globals.css`

**Interfaces:**
- Produces: Base buildable Next.js 14 application with Tailwind CSS, Three.js (`three`, `@types/three`), `lucide-react`, `framer-motion`, `clsx`, `tailwind-merge`.

- [ ] **Step 1: Create package.json and project configuration files**
- [ ] **Step 2: Install dependencies using npm**
- [ ] **Step 3: Setup tailwind.config.js with theme tokens and app/globals.css with smooth transitions**
- [ ] **Step 4: Verify Next.js build (`npm run build`) succeeds**
- [ ] **Step 5: Git commit task 1**

---

### Task 2: Theme Management System (Day / Night Mode)

**Files:**
- Create: `components/ThemeContext.tsx`
- Create: `lib/utils.ts`

**Interfaces:**
- Produces:
  ```typescript
  export type Theme = 'light' | 'dark';
  export interface ThemeContextType {
    theme: Theme;
    toggleTheme: () => void;
    setTheme: (theme: Theme) => void;
  }
  export const useTheme: () => ThemeContextType;
  export const ThemeProvider: React.FC<{ children: React.ReactNode }>;
  ```

- [ ] **Step 1: Create `lib/utils.ts` for cn helper**
- [ ] **Step 2: Implement `components/ThemeContext.tsx` with localStorage persistence and HTML class sync**
- [ ] **Step 3: Verify ThemeProvider works without SSR hydration mismatch**
- [ ] **Step 4: Git commit task 2**

---

### Task 3: Full-Size Three.js 3D Kinetic Sculpture Hero Canvas

**Files:**
- Create: `components/Hero3D.tsx`

**Interfaces:**
- Consumes: `useTheme()` from `components/ThemeContext.tsx`
- Produces:
  ```typescript
  export default function Hero3D(): JSX.Element;
  ```

- [ ] **Step 1: Implement Three.js scene with Crystalline Icosahedron, dual Torus orbital rings, and floating particle field**
- [ ] **Step 2: Implement smooth Lerp mouse tracking physics and ambient gyroscope animation**
- [ ] **Step 3: Implement real-time material & lighting transition between Day (Titanium/Chrome) and Night (Deep Obsidian Iridescent)**
- [ ] **Step 4: Implement ResizeObserver and WebGL memory disposal cleanup**
- [ ] **Step 5: Git commit task 3**

---

### Task 4: Floating Frosted Glass Navbar with Day/Night Switch

**Files:**
- Create: `components/Navbar.tsx`

**Interfaces:**
- Consumes: `useTheme()` from `components/ThemeContext.tsx`
- Produces:
  ```typescript
  export default function Navbar({ onOpenContact }: { onOpenContact: () => void }): JSX.Element;
  ```

- [ ] **Step 1: Implement floating frosted glass container with blur backdrop and responsive layout**
- [ ] **Step 2: Add AETHERIS geometric monogram logo and navigation links**
- [ ] **Step 3: Implement tactile Day/Night sliding toggle switch with Lucide icons**
- [ ] **Step 4: Add "Consult with Us" primary action button**
- [ ] **Step 5: Git commit task 4**

---

### Task 5: Hero Content & Interactive Metric Strip

**Files:**
- Create: `components/HeroContent.tsx`

**Interfaces:**
- Consumes: `onOpenContact: () => void`
- Produces:
  ```typescript
  export default function HeroContent({ onOpenContact }: { onOpenContact: () => void }): JSX.Element;
  ```

- [ ] **Step 1: Implement hero status pill and editorial typography headline**
- [ ] **Step 2: Implement primary & secondary interactive CTA buttons with hover micro-physics**
- [ ] **Step 3: Implement live architecture benchmark & latency status strip**
- [ ] **Step 4: Git commit task 5**

---

### Task 6: Strategic Architecture Pillars Bento Grid

**Files:**
- Create: `components/PillarsSection.tsx`

**Interfaces:**
- Produces:
  ```typescript
  export default function PillarsSection(): JSX.Element;
  ```

- [ ] **Step 1: Define pillar data structures with icons, descriptions, and metric tags**
- [ ] **Step 2: Build 4-card asymmetric Bento Grid layout with hover glow effects**
- [ ] **Step 3: Implement Light/Dark responsive card surface styling**
- [ ] **Step 4: Git commit task 6**

---

### Task 7: Interactive Capabilities Showcase

**Files:**
- Create: `components/InteractiveShowcase.tsx`

**Interfaces:**
- Produces:
  ```typescript
  export default function InteractiveShowcase(): JSX.Element;
  ```

- [ ] **Step 1: Implement interactive tab navigation (Cloud Architecture, Distributed Systems, Immersive UI, AI Governance)**
- [ ] **Step 2: Implement dynamic capability preview card with code/architecture highlights**
- [ ] **Step 3: Add smooth transition animations with Framer Motion**
- [ ] **Step 4: Git commit task 7**

---

### Task 8: Global Proof & Performance Metrics Section

**Files:**
- Create: `components/StatsSection.tsx`

**Interfaces:**
- Produces:
  ```typescript
  export default function StatsSection(): JSX.Element;
  ```

- [ ] **Step 1: Implement 4 high-impact metric counters (99.999% Reliability, 40ms Latency, $4.2B+ Volume, 140+ Deployments)**
- [ ] **Step 2: Add client partner logo marquee / typography ticker**
- [ ] **Step 3: Git commit task 8**

---

### Task 9: Consultation Scheduler & Interactive Contact Modal

**Files:**
- Create: `components/CTASection.tsx`
- Create: `components/ContactModal.tsx`

**Interfaces:**
- Produces:
  ```typescript
  export default function CTASection({ onOpenContact }: { onOpenContact: () => void }): JSX.Element;
  export default function ContactModal({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }): JSX.Element;
  ```

- [ ] **Step 1: Build high-impact CTA section with direct consultation triggers**
- [ ] **Step 2: Build accessible Contact Modal with form validation and feedback state**
- [ ] **Step 3: Git commit task 9**

---

### Task 10: Multi-Timezone Live Clocks Footer

**Files:**
- Create: `components/Footer.tsx`

**Interfaces:**
- Produces:
  ```typescript
  export default function Footer(): JSX.Element;
  ```

- [ ] **Step 1: Implement real-time live clock indicators for Bangkok (UTC+7), Tokyo (UTC+9), London (UTC+0), New York (UTC-5)**
- [ ] **Step 2: Add corporate links, system status indicator, and copyright**
- [ ] **Step 3: Git commit task 10**

---

### Task 11: Main Page Integration, Verification & Production Build

**Files:**
- Create: `app/layout.tsx`
- Create: `app/page.tsx`

**Interfaces:**
- Assembles all components into the cohesive landing page with SEO metadata.

- [ ] **Step 1: Build `app/layout.tsx` with ThemeProvider and font imports**
- [ ] **Step 2: Assemble `app/page.tsx` with all sections and modal state**
- [ ] **Step 3: Run full TypeScript and Next.js production build (`npm run build`)**
- [ ] **Step 4: Verify 60 FPS Three.js rendering and Day/Night toggle across all sections**
- [ ] **Step 5: Final Git commit**
