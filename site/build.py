#!/usr/bin/env python3
"""Builds the static website into a folder (default: build/site), one page per language.
Usage: python3 site/build.py [out_dir]
Texts: site/i18n/<lang>.json (same keys in every language); page: site/template.html ({{key}} placeholders)."""
import html, json, os, plistlib, re, shutil, sys

BASE = "https://sungvadan.github.io/ed-pdf/"
REPO = "https://github.com/sungvadan/ed-pdf"
SPONSORS = "https://github.com/sponsors/sungvadan"
PAYPAL = ""  # e.g. https://www.paypal.com/donate/?hosted_button_id=… ; no button while empty
LANGS = ["fr", "en"]  # the first one with a match in the browser's languages wins; "en" is the default
DEFAULT_LANG = "en"

SITE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SITE)
OUT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(ROOT, "build/site")

DOWNLOADS = {
    "mac": f"{REPO}/releases/latest/download/ed-pdf.dmg",
    "win_x64": f"{REPO}/releases/latest/download/ed-pdf-x64.exe",
    "win_arm": f"{REPO}/releases/latest/download/ed-pdf-arm64.exe",
}

# 24×24 stroke icons, one per feature.
ICONS = {
    "text": '<path d="M4 6h16M4 12h10M4 18h7"/><path d="M17 14l3 3-5 5h-3v-3z"/>',
    "highlight": '<path d="M4 20h16"/><path d="M6 15l9-9 3 3-9 9H6z"/>',
    "sign": '<path d="M3 17c3-6 5-9 7-9s0 8 2 8 3-5 5-5 2 3 4 3"/><path d="M3 21h18"/>',
    "pages": '<rect x="4" y="3" width="11" height="14" rx="1.5"/><path d="M9 21h9.5a1.5 1.5 0 0 0 1.5-1.5V8"/>',
    "form": '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h4"/>',
    "redact": '<path d="M4 7h16M4 12h6M4 17h16"/><rect x="12" y="10" width="8" height="4" fill="currentColor"/>',
    "send": '<path d="M21 3L10 14"/><path d="M21 3l-7 18-4-7-7-4z"/>',
    "read": '<circle cx="11" cy="11" r="6"/><path d="M20 20l-4.5-4.5"/>',
}


def esc(text):
    return html.escape(text, quote=True)


def version():
    with open(os.path.join(ROOT, "Resources/Info.plist"), "rb") as f:
        return plistlib.load(f)["CFBundleShortVersionString"]


def lookup(strings, key):
    value = strings
    for part in key.split("."):
        value = value[part]
    return value


def render(template, values, strings):
    def replace(match):
        key = match.group(1)
        if key in values:
            return values[key]
        value = lookup(strings, key)
        if isinstance(value, list):
            return "".join(f"<li>{esc(item)}</li>" for item in value)
        return esc(value)
    return re.sub(r"\{\{([a-z_.0-9]+)\}\}", replace, template)


def json_ld(strings, lang, url):
    data = {
        "@context": "https://schema.org",
        "@type": "SoftwareApplication",
        "name": "ed-pdf",
        "description": strings["meta"]["description"],
        "url": url,
        "image": BASE + "assets/og.jpg",
        "applicationCategory": "BusinessApplication",
        "operatingSystem": "macOS 14, Windows 10, Windows 11",
        "softwareVersion": version(),
        "downloadUrl": [DOWNLOADS["mac"], DOWNLOADS["win_x64"], DOWNLOADS["win_arm"]],
        "license": "https://opensource.org/licenses/MIT",
        "isAccessibleForFree": True,
        "offers": {"@type": "Offer", "price": "0", "priceCurrency": "EUR"},
        "inLanguage": lang,
        "codeRepository": REPO,
        "author": {"@type": "Person", "name": "svd", "url": "https://github.com/sungvadan"},
    }
    return json.dumps(data, ensure_ascii=False).replace("</", "<\\/")


def page(template, strings, all_strings, lang):
    url = f"{BASE}{lang}/"
    alternates = [f'<link rel="alternate" hreflang="{other}" href="{BASE}{other}/">' for other in LANGS]
    alternates.append(f'<link rel="alternate" hreflang="x-default" href="{BASE}">')
    languages = "".join(
        f'<a class="lang" href="../{other}/" hreflang="{other}" lang="{other}">{esc(all_strings[other]["meta"]["language_name"])}</a>'
        for other in LANGS if other != lang)
    cards = "".join(
        f'<article class="card"><svg viewBox="0 0 24 24" aria-hidden="true">{ICONS[item["icon"]]}</svg>'
        f'<h3>{esc(item["title"])}</h3><p>{esc(item["text"])}</p></article>'
        for item in strings["features"]["items"])
    privacy = "".join(f'<div><h3>{esc(p["title"])}</h3><p>{esc(p["text"])}</p></div>' for p in strings["privacy"])
    faq = "".join(
        f'<details><summary>{esc(item["q"])}</summary><p>{esc(item["a"])}</p></details>'
        for item in strings["faq"]["items"])
    donate = f'<a class="button primary" href="{SPONSORS}">{esc(strings["support"]["sponsors"])}</a>'
    if PAYPAL:
        donate += f'<a class="button primary" href="{esc(PAYPAL)}">{esc(strings["support"]["paypal"])}</a>'
    values = {
        "lang": lang, "lang_path": f"{lang}/", "url": url, "base": BASE, "root": "../", "repo": REPO,
        "version": esc(version()), "alternates": "\n".join(alternates), "languages": languages,
        "jsonld": json_ld(strings, lang, url), "feature_cards": cards, "privacy_points": privacy,
        "faq_items": faq, "donate_buttons": donate,
        "download.mac_url": DOWNLOADS["mac"], "download.win_x64_url": DOWNLOADS["win_x64"],
        "download.win_arm_url": DOWNLOADS["win_arm"],
    }
    return render(template, values, strings)


def root_page(all_strings, prefix="", noindex=False):
    """`/` (and the 404 page, with absolute links): sends the visitor to their language; search engines get the
    links and hreflang."""
    links = "".join(
        f'<li><a href="{prefix}{lang}/" hreflang="{lang}" lang="{lang}">{esc(all_strings[lang]["meta"]["language_name"])}</a></li>'
        for lang in LANGS)
    alternates = "".join(f'<link rel="alternate" hreflang="{lang}" href="{BASE}{lang}/">' for lang in LANGS)
    meta = all_strings[DEFAULT_LANG]["meta"]
    return f"""<!doctype html>
<html lang="{DEFAULT_LANG}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(meta["title"])}</title>
<meta name="description" content="{esc(meta["description"])}">
{'<meta name="robots" content="noindex">' if noindex else f'<link rel="canonical" href="{BASE}">'}
{alternates}<link rel="alternate" hreflang="x-default" href="{BASE}">
<link rel="icon" type="image/png" href="{prefix}assets/favicon.png">
<link rel="stylesheet" href="{prefix}assets/style.css">
<script>
(function () {{
  var available = {json.dumps(LANGS)};
  var wanted = navigator.languages || [navigator.language || ""];
  for (var i = 0; i < wanted.length; i++) {{
    var code = String(wanted[i]).toLowerCase().split("-")[0];
    if (available.indexOf(code) >= 0) {{ location.replace("{prefix}" + code + "/"); return; }}
  }}
  location.replace("{prefix}{DEFAULT_LANG}/");
}})();
</script>
</head>
<body class="chooser">
<main class="wrap"><h1><img src="{prefix}assets/icon-192.png" alt="" width="64" height="64"> ed-pdf</h1><ul>{links}</ul></main>
</body>
</html>
"""


def sitemap():
    entries = []
    for lang in LANGS:
        links = "".join(f'<xhtml:link rel="alternate" hreflang="{other}" href="{BASE}{other}/"/>' for other in LANGS)
        entries.append(f"<url><loc>{BASE}{lang}/</loc>{links}</url>")
    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n'
            + "\n".join(entries) + "\n</urlset>\n")


def main():
    all_strings = {lang: json.load(open(os.path.join(SITE, "i18n", f"{lang}.json"), encoding="utf-8")) for lang in LANGS}
    template = open(os.path.join(SITE, "template.html"), encoding="utf-8").read()
    shutil.rmtree(OUT, ignore_errors=True)
    shutil.copytree(os.path.join(SITE, "assets"), os.path.join(OUT, "assets"))
    for lang in LANGS:
        os.makedirs(os.path.join(OUT, lang))
        with open(os.path.join(OUT, lang, "index.html"), "w", encoding="utf-8") as f:
            f.write(page(template, all_strings[lang], all_strings, lang))
    with open(os.path.join(OUT, "index.html"), "w", encoding="utf-8") as f:
        f.write(root_page(all_strings))
    with open(os.path.join(OUT, "404.html"), "w", encoding="utf-8") as f:
        f.write(root_page(all_strings, prefix=BASE, noindex=True))
    with open(os.path.join(OUT, "sitemap.xml"), "w", encoding="utf-8") as f:
        f.write(sitemap())
    with open(os.path.join(OUT, "robots.txt"), "w", encoding="utf-8") as f:
        f.write(f"User-agent: *\nAllow: /\nSitemap: {BASE}sitemap.xml\n")
    open(os.path.join(OUT, ".nojekyll"), "w").close()
    print(f"Site généré dans {OUT} ({', '.join(LANGS)})")


main()
