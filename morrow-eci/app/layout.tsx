import type { Metadata } from "next";
import { Chakra_Petch, Geist, IBM_Plex_Mono, IBM_Plex_Sans } from "next/font/google";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

/**
 * The interface speaks in Chakra Petch and IBM Plex -- the faces the aperture
 * face was designed against, so the type and the eye come from the same
 * drawing. Display for headings and the persona's own labels, sans for
 * everything the interface says, mono for numbers and identifiers.
 *
 * Geist stays loaded for exactly one job: the words the person types. Their
 * side of the conversation is not the persona's voice and should not wear its
 * face, so `.morrow-hand` (globals.css) hands it back.
 */
const display = Chakra_Petch({
  variable: "--font-display",
  weight: ["400", "600", "700"],
  subsets: ["latin"],
});

const plexSans = IBM_Plex_Sans({
  variable: "--font-plex-sans",
  weight: ["400", "500", "600"],
  subsets: ["latin"],
});

const plexMono = IBM_Plex_Mono({
  variable: "--font-plex-mono",
  weight: ["400", "500"],
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
      className={`${geistSans.variable} ${display.variable} ${plexSans.variable} ${plexMono.variable} h-full antialiased`}
      suppressHydrationWarning
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
      </head>
      <body className="min-h-full flex flex-col">{children}</body>
    </html>
  );
}
