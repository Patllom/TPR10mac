import type { Metadata } from 'next';
import './globals.css';
import { ThemeProvider } from '../components/ThemeContext';

export const metadata: Metadata = {
  title: 'TPR-10 Co., Ltd. | Integrated IT, Network, AI, and Water Information Solutions',
  description:
    'TPR-10 delivers end-to-end technology solutions: telemetering support for RID & DWR, web & application development, enterprise AI platforms, private networks, and cybersecurity.',
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning className="dark">
      <body className="font-sans antialiased min-h-screen selection:bg-orange-500 selection:text-white">
        <ThemeProvider>
          {children}
        </ThemeProvider>
      </body>
    </html>
  );
}
