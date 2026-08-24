import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Relay — durable runtime for AI coding agents",
  description:
    "Relay keeps AI coding agents running when your laptop, network, or SSH session doesn't.",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        <div className="container">
          <header className="topbar">
            <span className="logo">⚡ Relay</span>
            <span className="tagline">
              durable runtime for AI coding agents
            </span>
          </header>
          {children}
        </div>
      </body>
    </html>
  );
}
