#!/usr/bin/env python3
"""Regenerate barakoCMS module logos in a flat multicolor (icons8 "Color") style:
flat solid rounded-square badge per module, chunky filled glyphs in white plus a
bright gold accent, softly rounded, no gradients, no outlines-as-shapes, no shadows."""
import os

GOLD = "#FFC53D"
WHITE = "#FFFFFF"

# module dir -> (label, flat background color, glyph svg fragment)
def wrap(label, bg, glyph):
    return f'''<svg width="128" height="128" viewBox="0 0 128 128" fill="none" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="{label}">
  <title>{label}</title>
  <rect x="2" y="2" width="124" height="124" rx="28" fill="{bg}"/>
{glyph}
</svg>
'''

ICONS = {}

# AI: flat chip. White chip body, gold core, white pins.
ICONS["BarakoCMS.AI"] = ("BarakoCMS.AI", "#6A2C8F", f'''  <g fill="{WHITE}">
    <rect x="52" y="30" width="8" height="14" rx="4"/><rect x="68" y="30" width="8" height="14" rx="4"/>
    <rect x="52" y="84" width="8" height="14" rx="4"/><rect x="68" y="84" width="8" height="14" rx="4"/>
    <rect x="30" y="52" width="14" height="8" rx="4"/><rect x="30" y="68" width="14" height="8" rx="4"/>
    <rect x="84" y="52" width="14" height="8" rx="4"/><rect x="84" y="68" width="14" height="8" rx="4"/>
  </g>
  <rect x="40" y="40" width="48" height="48" rx="12" fill="{WHITE}"/>
  <rect x="54" y="54" width="20" height="20" rx="6" fill="{GOLD}"/>''')

# Accounting: balance scale, white frame, gold pans.
ICONS["BarakoCMS.Accounting"] = ("BarakoCMS.Accounting", "#7A5230", f'''  <g fill="{WHITE}">
    <rect x="60" y="30" width="8" height="66" rx="4"/>
    <rect x="34" y="40" width="60" height="8" rx="4"/>
    <rect x="44" y="94" width="40" height="9" rx="4"/>
    <circle cx="64" cy="32" r="7"/>
  </g>
  <path d="M34 44 L22 68 H46 Z" fill="{GOLD}"/>
  <path d="M94 44 L82 68 H106 Z" fill="{GOLD}"/>''')

# Analytics.Umami: bar chart, white bars + gold tall bar.
ICONS["BarakoCMS.Analytics.Umami"] = ("BarakoCMS.Analytics.Umami", "#E91E63", f'''  <rect x="32" y="70" width="18" height="26" rx="5" fill="{WHITE}"/>
  <rect x="55" y="54" width="18" height="42" rx="5" fill="{WHITE}"/>
  <rect x="78" y="34" width="18" height="62" rx="5" fill="{GOLD}"/>''')

# DeviceTrust: shield with check.
ICONS["BarakoCMS.DeviceTrust"] = ("BarakoCMS.DeviceTrust", "#D32F2F", f'''  <path d="M64 22 L34 33 V63 C34 84 49 103 64 110 C79 103 94 84 94 63 V33 Z" fill="{WHITE}"/>
  <path d="M54 64 L61 72 L77 52" fill="none" stroke="{GOLD}" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/>''')

# Diagnostics: card with heartbeat.
ICONS["BarakoCMS.Diagnostics"] = ("BarakoCMS.Diagnostics", "#455A64", f'''  <rect x="26" y="40" width="76" height="48" rx="12" fill="{WHITE}"/>
  <path d="M36 64 H50 L57 48 L71 80 L78 64 H92" fill="none" stroke="{GOLD}" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>''')

# Email.Resend: envelope, white body + gold flap.
ICONS["BarakoCMS.Email.Resend"] = ("BarakoCMS.Email.Resend", "#1976D2", f'''  <rect x="30" y="42" width="68" height="44" rx="10" fill="{WHITE}"/>
  <path d="M34 48 L64 70 L94 48" fill="none" stroke="{GOLD}" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>''')

# Email.Smtp: envelope in flight, white body + gold speed lines.
ICONS["BarakoCMS.Email.Smtp"] = ("BarakoCMS.Email.Smtp", "#00796B", f'''  <rect x="44" y="42" width="60" height="44" rx="10" fill="{WHITE}"/>
  <path d="M48 48 L74 68 L100 48" fill="none" stroke="{GOLD}" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>
  <g stroke="{GOLD}" stroke-width="8" stroke-linecap="round">
    <path d="M16 52 H34"/><path d="M22 64 H34"/><path d="M16 76 H34"/>
  </g>''')

# ExternalAuth: key, gold ring + white shaft.
ICONS["BarakoCMS.ExternalAuth"] = ("BarakoCMS.ExternalAuth", "#F57C00", f'''  <circle cx="54" cy="54" r="20" fill="{WHITE}"/>
  <circle cx="54" cy="54" r="9" fill="#F57C00"/>
  <g fill="{GOLD}">
    <rect x="62" y="66" width="12" height="34" rx="4" transform="rotate(-45 62 66)"/>
    <rect x="80" y="80" width="16" height="10" rx="4" transform="rotate(-45 80 80)"/>
  </g>''')

# FeatureFlags: two toggles, white tracks + gold knobs.
ICONS["BarakoCMS.FeatureFlags"] = ("BarakoCMS.FeatureFlags", "#7B1FA2", f'''  <rect x="30" y="38" width="68" height="26" rx="13" fill="{WHITE}"/>
  <circle cx="83" cy="51" r="9" fill="{GOLD}"/>
  <rect x="30" y="72" width="68" height="26" rx="13" fill="{WHITE}"/>
  <circle cx="45" cy="85" r="9" fill="#7B1FA2"/>''')

# Files: folder, white body + gold tab.
ICONS["BarakoCMS.Files"] = ("BarakoCMS.Files", "#607D8B", f'''  <path d="M28 46 C28 42 31 39 35 39 H52 L60 48 H93 C97 48 100 51 100 55 V60 H28 Z" fill="{GOLD}"/>
  <rect x="28" y="54" width="72" height="40" rx="9" fill="{WHITE}"/>''')

# Files.S3: cloud, white cloud + gold up arrow.
ICONS["BarakoCMS.Files.S3"] = ("BarakoCMS.Files.S3", "#0277BD", f'''  <path d="M44 84 A18 18 0 0 1 43 48 A26 26 0 0 1 88 50 A16 16 0 0 1 86 84 Z" fill="{WHITE}"/>
  <path d="M64 78 V56 M55 64 L64 55 L73 64" fill="none" stroke="{GOLD}" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>''')

# Import: arrow down into a tray.
ICONS["BarakoCMS.Import"] = ("BarakoCMS.Import", "#2B5B8A", f'''  <path d="M64 28 V66 M50 52 L64 68 L78 52" fill="none" stroke="{GOLD}" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M32 74 V92 C32 96 35 99 39 99 H89 C93 99 96 96 96 92 V74" fill="none" stroke="{WHITE}" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/>''')

# Portability: box with up + down arrows.
ICONS["BarakoCMS.Portability"] = ("BarakoCMS.Portability", "#5D4037", f'''  <rect x="40" y="46" width="48" height="44" rx="10" fill="{WHITE}"/>
  <path d="M54 68 V54 M48 60 L54 53 L60 60" fill="none" stroke="#5D4037" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M74 68 V82 M68 76 L74 83 L80 76" fill="none" stroke="{GOLD}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>''')

# Pwa: phone with install arrow.
ICONS["BarakoCMS.Pwa"] = ("BarakoCMS.Pwa", "#009688", f'''  <rect x="42" y="22" width="44" height="84" rx="12" fill="{WHITE}"/>
  <circle cx="64" cy="96" r="4" fill="#009688"/>
  <path d="M64 44 V70 M53 60 L64 71 L75 60" fill="none" stroke="{GOLD}" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>''')

# Repo root is the parent of the scripts/ directory this file lives in, so the
# generator works from any checkout location.
base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
for mod, (label, bg, glyph) in ICONS.items():
    path = os.path.join(base, mod, "assets", "icon.svg")
    if not os.path.isdir(os.path.dirname(path)):
        print("SKIP missing dir:", path)
        continue
    open(path, "w").write(wrap(label, bg, glyph))
    print("wrote", path)
