import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "ECI-CAS Companion",
  description: "Mock shell for the ECI-CAS companion app (M5/M6/M7 review).",
};

/**
 * Runs before first paint, ahead of hydration, so the page never flashes the
 * wrong theme. It has to be inline and blocking for that: a React effect
 * would only fire after the server-rendered markup is already on screen.
 * Hence `suppressHydrationWarning` on <html> — the server can't know which
 * attribute this will set, and that mismatch is expected rather than a bug.
 */
const themeScript = `
try {
  var stored = localStorage.getItem("theme");
  var theme = stored === "light" || stored === "dark"
    ? stored
    : (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  document.documentElement.dataset.theme = theme;
} catch (e) {
  document.documentElement.dataset.theme = "light";
}
`;

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
      suppressHydrationWarning
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
      </head>
      <body className="min-h-full flex flex-col">{children}</body>
    </html>
  );
}
