#!/usr/bin/env python3
"""Checks the translations of the UI.

The French source text is the key (see Sources/Folio/Localization.swift). This script collects
every literal of Sources/Folio that looks like UI text and checks that each language in
Resources/Localizations has it, with the same format specifiers.

    scripts/l10n.py            check every language (exit 1 on problems)
    scripts/l10n.py keys       print all keys as a .strings file (French = French)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCES = sorted((ROOT / "Sources/Folio").glob("*.swift"))
# Windows app: only the Tr("…") calls are UI text.
WINDOWS_SOURCES = sorted(p for p in (ROOT / "windows").rglob("*.cs") if "/obj/" not in str(p) and "/bin/" not in str(p))
WINDOWS_TR = re.compile(r'\bTr\("((?:[^"\\]|\\.)*)"')
LOCALIZATIONS = ROOT / "Resources/Localizations"

# Lines whose literals are not UI text.
IGNORED_LINES = re.compile(
    r"subtype|forKey|forAnnotationKey|Key\(|applyingFilter|NSFont\(name|"
    r"CTFontCreateWithName|\.custom\(|fatalError|environment|defaultsKey|authorNameKey|"
    r"firstIndex\(of: \"--|appendingPathComponent|bundleIdentifier|AppleLanguages|"
    r"// l10n-ignore"
)
IGNORED_FILES = {"SelfTest.swift"}
LITERAL = re.compile(r'"((?:[^"\\]|\\.)*)"')
SPECIFIER = re.compile(r"%(?:\d+\$)?[-+ #0]*\d*(?:\.\d+)?(?:ll|l|h)?[dDuUxXoOfeEgGcCsSpaA@]")


def looks_like_text(s: str) -> bool:
    s = SPECIFIER.sub("", s).strip()
    if not re.search(r"[A-Za-zÀ-ÿ]", s):
        return False
    if re.search(r"[^\x00-\x7f]", s) or " " in s:
        return True
    return bool(re.fullmatch(r"[A-ZÀ-Ý][a-zà-ÿ]+", s))


# Single words that are names, not text to translate.
NOT_TEXT = {
    "Helvetica", "Arial", "Georgia", "Courier", "Verdana", "Menlo", "Times New Roman", "Avenir Next",
    "Highlight", "Underline", "StrikeOut", "FreeText", "Text", "Ink", "Square", "Circle", "Line",
    "Link", "Widget", "Popup", "Stamp", "Helvetica-Bold", "Page", "Document", "Pages", "Ellipse",
    "Rectangle", "Note", "Image", "Signature", "Format",
}
# Words above that are also UI text somewhere: kept as keys when found in a UI position.
UI_POSITION = re.compile(
    r'(Text|Button|Label|Toggle|Picker|Menu|Section|TextField|SecureField|ColorPicker|LabeledContent|'
    r'Stepper|CommandMenu|help|tr|actionName:|setActionName|change|name:|case \.\w+:)\(?\s*"$'
)


def source_keys():
    keys, problems = {}, []
    for path in SOURCES:
        if path.name in IGNORED_FILES:
            continue
        for number, line in enumerate(path.read_text().splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith("//") or IGNORED_LINES.search(line):
                continue
            code = line.split(" // ")[0]
            for match in LITERAL.finditer(code):
                text = match.group(1)
                prefix = code[: match.start() + 1]
                if re.search(r"(systemImage|systemName): \"$", prefix):
                    continue
                in_tr = re.search(r"\btr\(\"$", prefix) and re.search(r"[A-Za-zÀ-ÿ]", text)
                if not looks_like_text(text) and not in_tr:
                    continue
                if text in NOT_TEXT and not UI_POSITION.search(prefix):
                    continue
                where = f"{path.name}:{number}"
                if "\\(" in text:
                    problems.append(f"{where}: interpolation dans un texte traduisible, utiliser tr(\"…%d…\", x) : \"{text}\"")
                    continue
                keys.setdefault(unescape(text), where)
    for path in WINDOWS_SOURCES:
        for number, line in enumerate(path.read_text().splitlines(), 1):
            if line.strip().startswith("//"):
                continue
            for match in WINDOWS_TR.finditer(line):
                keys.setdefault(unescape(match.group(1)), f"windows/{path.relative_to(ROOT / 'windows')}:{number}")
    return keys, problems


def unescape(s: str) -> str:
    return s.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")


def escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


ENTRY = re.compile(r'^\s*"((?:[^"\\]|\\.)*)"\s*=\s*"((?:[^"\\]|\\.)*)"\s*;\s*$')


def read_strings(path: Path):
    entries = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.strip().startswith(("/*", "//", "*")):
            continue
        m = ENTRY.match(line)
        if not m:
            raise SystemExit(f"{path}:{number}: ligne illisible")
        entries[unescape(m.group(1))] = unescape(m.group(2))
    return entries


def specifiers(s: str):
    return sorted(re.sub(r"\d+\$", "", m) for m in SPECIFIER.findall(s.replace("%%", "")))


def main():
    keys, problems = source_keys()
    if len(sys.argv) > 1 and sys.argv[1] == "keys":
        for key in sorted(keys, key=str.lower):
            print(f'/* {keys[key]} */\n"{escape(key)}" = "{escape(key)}";\n')
        return
    for lproj in sorted(LOCALIZATIONS.glob("*.lproj")):
        strings = lproj / "Localizable.strings"
        if lproj.name == "fr.lproj" or not strings.exists():
            continue
        entries = read_strings(strings)
        language = lproj.stem
        for key, where in keys.items():
            if key not in entries:
                problems.append(f"{language}: manquant ({where}) : \"{key}\"")
            elif specifiers(key) != specifiers(entries[key]):
                problems.append(f"{language}: formats différents : \"{key}\" → \"{entries[key]}\"")
        for key in entries:
            if key not in keys:
                problems.append(f"{language}: inutilisé : \"{key}\"")
    for problem in problems:
        print(problem)
    print(f"{len(keys)} textes, {len(problems)} problème(s)")
    sys.exit(1 if problems else 0)


main()
