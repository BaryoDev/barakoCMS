import type { Metadata } from "next";
import { Sora, Manrope, JetBrains_Mono } from "next/font/google";
import "./globals.css";

// Signal theme (#407). Sora for display, Manrope for body, JetBrains Mono for anything a machine
// produced. Self-hosted by next/font, so no request leaves the box at runtime, which is the same
// reason the Yeti fonts were loaded this way.
const sora = Sora({
  variable: "--font-sora",
  subsets: ["latin"],
  weight: ["600"],
});

const manrope = Manrope({
  variable: "--font-manrope",
  subsets: ["latin"],
  weight: ["400", "500", "600", "700", "800"],
});

const jetbrainsMono = JetBrains_Mono({
  variable: "--font-jetbrains-mono",
  subsets: ["latin"],
  weight: ["400", "500", "700"],
});

declare global {
  interface Window {
    _env_?: {
      NEXT_PUBLIC_API_URL?: string;
    };
  }
}

import QueryProvider from "@/components/query-provider";
import { ThemeProvider } from "@/components/theme-provider";
import ErrorReporter from "@/components/error-reporter";
import { Toaster } from "sonner";

export const metadata: Metadata = {
  title: "BarakoCMS Admin",
  description: "Headless CMS Admin Dashboard",
};

// The app is served under this basePath (set at build). The runtime env-config.js is a static asset
// under it, so the script src must include the basePath — otherwise, when the admin is hosted on a
// different origin than it was built for, the config 404s and getApiUrl() falls back to the baked URL.
const basePath = process.env.NEXT_BASE_PATH || "";

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <head>
        {/* Runtime env must load before hydration so getApiUrl() sees overrides. */}
        {/* eslint-disable-next-line @next/next/no-sync-scripts */}
        <script src={`${basePath}/env-config.js`} />
      </head>
      <body
        className={`${sora.variable} ${manrope.variable} ${jetbrainsMono.variable} antialiased`}
      >
        {/* Pinned to light. Dark mode was deferred with the Signal redesign (#407), so forcing it here
            is what stops a stored "dark" preference, or an OS setting, from rendering the old Yeti
            palette against Signal components. Drop forcedTheme and restore enableSystem when a dark
            palette is actually drawn. */}
        <ThemeProvider attribute="class" defaultTheme="light" forcedTheme="light" disableTransitionOnChange>
          <QueryProvider>
            <ErrorReporter />
            {children}
            <Toaster richColors position="top-right" />
          </QueryProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
