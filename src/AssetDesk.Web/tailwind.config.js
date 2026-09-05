/** @type {import('tailwindcss').Config} */
// Mirrors the config that used to live inline in wwwroot/index.html next to the Tailwind Play
// CDN script. The CDN generated this stylesheet in the browser on every load; it is compiled
// ahead of time now, so this file is the only place the theme is defined.
//
// Colours carry <alpha-value> so opacity modifiers (bg-primary/10, border-border/50) resolve to
// a real alpha instead of collapsing to the opaque colour. The CSS variables they read are HSL
// triplets - see the :root / .dark blocks in Styles/app.css.
module.exports = {
  darkMode: 'class',
  content: [
    './**/*.{razor,html}',
    '!./bin/**',
    '!./obj/**',
    '!./node_modules/**'
  ],
  theme: {
    extend: {
      colors: {
        primary: {
          DEFAULT: 'hsl(var(--primary) / <alpha-value>)',
          foreground: 'hsl(var(--primary-foreground) / <alpha-value>)',
          50: '#eef2ff',
          100: '#e0e7ff',
          200: '#c7d2fe',
          300: '#a5b4fc',
          400: '#818cf8',
          500: '#6366f1',
          600: '#4f46e5',
          700: '#4338ca',
          800: '#3730a3',
          900: '#312e81',
          950: '#1e1b4b',
        },
        // shadcn/ui tokens consumed by Components/UI/* (Card, Button, Badge, Dialog, Select,
        // Input, Table, ...). Values come from the CSS variables defined in Styles/app.css,
        // which supplies both the light (:root) and dark (.dark) values.
        background: 'hsl(var(--background) / <alpha-value>)',
        foreground: 'hsl(var(--foreground) / <alpha-value>)',
        card: {
          DEFAULT: 'hsl(var(--card) / <alpha-value>)',
          foreground: 'hsl(var(--card-foreground) / <alpha-value>)',
        },
        popover: {
          DEFAULT: 'hsl(var(--popover) / <alpha-value>)',
          foreground: 'hsl(var(--popover-foreground) / <alpha-value>)',
        },
        secondary: {
          DEFAULT: 'hsl(var(--secondary) / <alpha-value>)',
          foreground: 'hsl(var(--secondary-foreground) / <alpha-value>)',
        },
        muted: {
          DEFAULT: 'hsl(var(--muted) / <alpha-value>)',
          foreground: 'hsl(var(--muted-foreground) / <alpha-value>)',
        },
        accent: {
          DEFAULT: 'hsl(var(--accent) / <alpha-value>)',
          foreground: 'hsl(var(--accent-foreground) / <alpha-value>)',
        },
        destructive: {
          DEFAULT: 'hsl(var(--destructive) / <alpha-value>)',
          foreground: 'hsl(var(--destructive-foreground) / <alpha-value>)',
        },
        border: 'hsl(var(--border) / <alpha-value>)',
        input: 'hsl(var(--input) / <alpha-value>)',
        ring: 'hsl(var(--ring) / <alpha-value>)',
        // Sidebar surface tokens - the nav is its own surface in shadcn, a shade off the page
        // background with its own border and accent.
        sidebar: {
          DEFAULT: 'hsl(var(--sidebar) / <alpha-value>)',
          foreground: 'hsl(var(--sidebar-foreground) / <alpha-value>)',
          primary: 'hsl(var(--sidebar-primary) / <alpha-value>)',
          'primary-foreground': 'hsl(var(--sidebar-primary-foreground) / <alpha-value>)',
          accent: 'hsl(var(--sidebar-accent) / <alpha-value>)',
          'accent-foreground': 'hsl(var(--sidebar-accent-foreground) / <alpha-value>)',
          border: 'hsl(var(--sidebar-border) / <alpha-value>)',
          ring: 'hsl(var(--sidebar-ring) / <alpha-value>)',
        },
      },
      // Tailwind's preflight paints every element's border-color with borderColor.DEFAULT
      // (gray-200) and divideColor.DEFAULT likewise. Extending `colors` above does NOT change
      // those defaults, so the many components that use a bare `border` / `border-b` /
      // `divide-y` (Card, Dialog, Alert, Badge, Table, DropdownMenu, ...) drew a near-white
      // edge that ignored the theme and stood out badly in dark mode. Point both defaults at
      // the themed token so an uncoloured border follows --border in light and dark alike.
      borderColor: {
        DEFAULT: 'hsl(var(--border) / <alpha-value>)',
      },
      divideColor: {
        DEFAULT: 'hsl(var(--border) / <alpha-value>)',
      },
      animation: {
        'fade-in': 'fadeIn 0.3s ease-out',
        'slide-up': 'slideUp 0.3s ease-out',
        'slide-down': 'slideDown 0.2s ease-out',
        'scale-in': 'scaleIn 0.2s ease-out',
      },
      keyframes: {
        fadeIn: {
          '0%': { opacity: '0' },
          '100%': { opacity: '1' },
        },
        slideUp: {
          '0%': { opacity: '0', transform: 'translateY(10px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        slideDown: {
          '0%': { opacity: '0', transform: 'translateY(-10px)' },
          '100%': { opacity: '1', transform: 'translateY(0)' },
        },
        scaleIn: {
          '0%': { opacity: '0', transform: 'scale(0.95)' },
          '100%': { opacity: '1', transform: 'scale(1)' },
        },
      },
    },
  },
  plugins: [],
}
