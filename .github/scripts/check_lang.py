#!/usr/bin/env python3
"""Checks that every text the code asks for is in assets/embalmedeats/lang/en.json.

Finds T(lang, "key"...) / DoctorText.T(..., "key"...) (texts "embalmedeats:doctor-<key>") and
Lang.Get/GetL/HasTranslation("embalmedeats:<key>") in the C# code. Keys put together at run time ("doctor-reason-" + reason) are checked by the self-test instead.
Also checks that a text's {n} places are no more than the arguments passed, where the call is on one line.
Exit 1 when something is missing.
"""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parents[2]
lang = json.loads((root / "assets/embalmedeats/lang/en.json").read_text(encoding="utf-8"))

doctor_call = re.compile(r'\bT\(\s*(?:lang|lang \?\? "en"|"en")\s*,\s*"([a-z0-9-]+)"((?:[^()]|\([^()]*\))*)\)')
full_key = re.compile(r'Lang\.(?:Get|GetL|HasTranslation)\((?:[^,()"]+,\s*)?"embalmedeats:([a-z0-9-]+)"')

problems = []
seen = 0
doctor_key = re.compile(r'\bT\(\s*(?:lang|lang \?\? "en"|"en")\s*,\s*"([a-z0-9-]+)"')

for cs in sorted((root / "src").rglob("*.cs")):
    text = cs.read_text(encoding="utf-8")
    where = cs.relative_to(root)
    for m in doctor_key.finditer(text):
        if m.group(1).endswith("-"):  # a prefix put together at run time
            continue
        seen += 1
        if "doctor-" + m.group(1) not in lang:
            problems.append(f"{where}: missing text doctor-{m.group(1)}")
    # The number of arguments, where the call is simple enough to read (one level of brackets inside)
    for m in doctor_call.finditer(text):
        key = "doctor-" + m.group(1)
        if key not in lang:
            continue
        places = {int(n) for n in re.findall(r"\{(\d+)\}", lang[key])}
        rest = m.group(2).strip()
        args = 0 if not rest else len([a for a in re.split(r",(?![^(]*\))", rest.lstrip(",")) if a.strip()])
        if places and max(places) >= args:
            problems.append(f"{where}: {key} has {{{max(places)}}} but only {args} argument(s) are passed")
    for m in full_key.finditer(text):
        key = m.group(1)
        if key.endswith("-"):
            continue
        seen += 1
        if key not in lang:
            problems.append(f"{where}: missing text {key}")

print(f"{seen} text uses checked against {len(lang)} texts")
for p in problems:
    print("::error::" + p)
sys.exit(1 if problems else 0)
