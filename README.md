# TPR-10 Landing Page

Corporate landing page for TPR-10 Co., Ltd., built with Next.js 14, React,
TypeScript, and Tailwind CSS.

## Current runtime

The active page is assembled in `app/page.tsx` from these sections:

- Hero and telemetry overview
- Water resources and telemetering services
- Digital solutions and AI platforms
- Infrastructure, network, and security
- Contact CTA and footer

The older AETHERIS prototype is preserved in `archive/aetheris/` and is
excluded from TypeScript compilation and the active runtime.

## Commands

```bash
npm install
npm run dev
npm run lint
npm run build
npm run start
```

The development server runs on `http://localhost:4000`. The production
server runs on `http://localhost:4001` after `npm run build`.

## Known product boundaries

- Telemetry values shown in the hero are simulated client-side demo data.
- The contact form currently displays a local success state; it is not wired
  to an email service or backend endpoint.
- Marketing figures and certification language should be confirmed by the
  business owner before production publication.
