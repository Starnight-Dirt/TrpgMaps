#!/usr/bin/env python3
# -*- coding: utf-8 -*-
#
# TrpgMaps - i18n guard (build-time, source level).
#
# Why this exists
# ---------------
# v1.0.3 shipped a language switcher, but a whole batch of user-visible strings
# were still hardcoded in C# (status lines, message boxes, the file-picker, port
# warnings, the update dialogs...). The word list looked complete because only
# *some* of the code asked for words from it. Switching to English then left
# half the window in Chinese.
#
# So this script fails the build when:
#   1. a key used by the code is not defined in Core/I18n.cs (typo / forgotten key)
#   2. a key is defined twice (the second one silently wins in the JSON files)
#   3. a key in locales/*.json is missing, or its value is empty
#   4. a UI string is hardcoded in Chinese again (the whole point of the exercise)
#
# Check 4 only looks at lines that *put text on screen*: .Text =, MakeLabel(,
# MakeButton(, MessageBox.Show(, SetStatus(... and so on. Log lines, comments and
# anything that already calls Loc.T are ignored, so diagnostics may stay Chinese.
#
# This file must stay pure ASCII (same reason as build.ps1: Windows PowerShell /
# old tooling on a Chinese code page mangling non-ASCII bytes).
#
# Usage:  python scripts/check_i18n.py            (exit 0 = clean, 1 = problems)
#         python scripts/check_i18n.py --warn     (exit 0 even with problems)

import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
I18N_CS = os.path.join(ROOT, "Core", "I18n.cs")
LOCALES = os.path.join(ROOT, "locales")

SKIP_DIRS = {
    "bin", "obj", "build_logs", "installer", ".git", "Resources", "updater",
    "wwwroot", "entity", "item", "terrain", "maps", "locales", "native",
    "scripts", "Properties", "_probe",
}

CJK = re.compile(u"[\u4e00-\u9fff]")

# Keys are always the first argument, so a plain literal match is enough.
ADD_KEY = re.compile(r't\.Add\(\s*"([A-Za-z0-9_.\-]+)"\s*,')
USE_KEY = re.compile(r'Loc\.T\(\s*"([A-Za-z0-9_.\-]+)"')
TEXTIN_KEY = re.compile(r'Loc\.TextIn\(\s*[^,]+,\s*"([A-Za-z0-9_.\-]+)"')

# Lines that put text in front of the user.
UI_CALL = re.compile(
    r"(\.Text\s*=|MakeLabel\(|MakeButton\(|SetStatus\(|SetNetStatus\(|"
    r"SetAudioStatus\(|MessageBox\.Show\(|ShowWarning\(|ShowInfo\(|"
    r"\.Title\s*=|\.Filter\s*=|SetItems\(|AddTitle\(|AddLinkRow\(|"
    r"DrawString\(|DrawText\(|SetToast\()"
)


def read(path):
    with io.open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def cs_files():
    for base, dirs, names in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in sorted(names):
            if name.endswith(".cs"):
                yield os.path.join(base, name)


def main():
    problems = []
    warnings = []

    if not os.path.isfile(I18N_CS):
        print("FAIL  Core/I18n.cs not found")
        return 1

    source = read(I18N_CS)

    # ---- 1/2. keys defined in the built-in table ----
    defined = []
    duplicated = []
    for match in ADD_KEY.finditer(source):
        key = match.group(1)
        if key in defined:
            duplicated.append(key)
        else:
            defined.append(key)

    for key in duplicated:
        problems.append("duplicate key in Core/I18n.cs: " + key)

    defined_set = set(defined)

    # ---- 3. locale files ----
    for code in ("zh-cn", "en-us"):
        path = os.path.join(LOCALES, code + ".json")
        if not os.path.isfile(path):
            problems.append("missing locale file: locales/" + code + ".json")
            continue
        try:
            data = json.loads(read(path))
        except Exception as exc:                       # noqa: BLE001
            problems.append("bad JSON in locales/" + code + ".json: " + str(exc))
            continue
        for key in defined:
            if key not in data:
                problems.append("locales/" + code + ".json is missing key: " + key)
            elif not str(data[key]).strip():
                problems.append("locales/" + code + ".json has an empty value: " + key)

    # ---- 4. keys used by the code ----
    used = set()
    for path in cs_files():
        name = os.path.relpath(path, ROOT).replace("\\", "/")
        if name in ("Core/I18n.cs", "SelfCheck.cs"):
            continue
        text = read(path)
        for match in USE_KEY.finditer(text):
            used.add(match.group(1))
        for match in TEXTIN_KEY.finditer(text):
            used.add(match.group(1))

        # hardcoded UI strings
        for number, line in enumerate(text.splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("*"):
                continue
            if "Loc.T(" in line or "Loc.TextIn(" in line:
                continue
            if not UI_CALL.search(line):
                continue
            if not CJK.search(line):
                continue
            warnings.append("%s:%d: %s" % (name, number, stripped[:120]))

    for key in sorted(used - defined_set):
        problems.append("key used by the code but not defined in Core/I18n.cs: " + key)

    # ---- report ----
    print("i18n guard")
    print("  keys defined : %d" % len(defined))
    print("  keys used    : %d" % len(used))

    if warnings:
        print("")
        print("  hardcoded (Chinese) UI strings still found: %d" % len(warnings))
        for item in warnings:
            print("    " + item)

    if problems:
        print("")
        print("  problems: %d" % len(problems))
        for item in problems:
            print("    " + item)

    if problems or warnings:
        print("")
        print("I18N CHECK FAILED")
        return 0 if "--warn" in sys.argv else 1

    print("")
    print("I18N CHECK PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
