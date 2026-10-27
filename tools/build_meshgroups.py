#!/usr/bin/env python3
"""Builds the mesh group catalogue files PlayerModelChanger reads.

The mesh groups of a compiled model (vmdl_c) live in its DATA block:

    m_meshGroups = [ "family_@0_#&variant", ... ]
    m_nDefaultMeshGroupMask = 21

The order of m_meshGroups is the mesh group index that the game uses in
CModelState::m_nMeshGroupMask, so the catalogue keeps that order.

Two steps:

    extract  reads the DATA block of every model of a VPK (Source2Viewer CLI)
    build    turns the raw data into the catalogue the plugin embeds

Usage:

    python build_meshgroups.py extract --vpk <dir.vpk> --out raw.json
    python build_meshgroups.py build   --raw raw.json --out <csgo>/addons/counterstrikesharp/configs/plugins/PlayerModelChanger-RE/catalogue/<addon>.json

The plugin ships no catalogue of its own: it reads every JSON file of the
"catalogue" directory next to PlayerModelChanger.json, so the files of every
addon of the server have to be built and put there. A file that sorts later
wins over an earlier one.

When "cli-windows-x64/Source2Viewer-CLI.exe" sits next to the plugin, the
plugin can do this itself in the background: when "MeshgroupCatalogueWorkshopIds"
or "MeshgroupCatalogueVpkDirs" is set in PlayerModelChanger.json, it reads the
configured addons / folders and writes such a file into the "auto" folder of the
catalogue directory. Without that configuration nothing is scanned. The files of
the server owner in the catalogue directory win over the generated ones.

Scanning every addon folder takes a while on a server with many addons, so the
server owner can list the workshop ids of the addons that hold character models
in "MeshgroupCatalogueWorkshopIds" in PlayerModelChanger.json. Only those are
read then; an empty list scans every addon folder.

The catalogue holds, for every model:

    default     the default mesh group mask of the model
    categories  the mesh groups the player may pick, grouped by family
    fixed       the mesh groups that always keep the value of the model

The families "first_or_third_person*", "face*" and "defusekit" are not part
of the categories: they are part of the model itself (the view the model is
drawn in, the face and the defuse kit of the character) and stay on the value
the model was made with. The same goes for "autodefault", the group that keeps
the whole model visible.
"""

import argparse
import json
import os
import re
import subprocess
import sys

# The families that the player may not change.
LOCKED_PREFIXES = ("first_or_third_person", "face")
LOCKED_EXACT = ("autodefault", "defusekit")

CLI = r"D:\cs2kz_server\cli-windows-x64\Source2Viewer-CLI.exe"

MARKER = re.compile(r"^\[(\d+)/(\d+)\]\s+(.+?)\s*$")
LIST_ENTRY = re.compile(r"^(\S+)\s+crc=0x[0-9a-f]+\s+metadatasz=\d+\s+fnumber=\d+\s+ofs=0x[0-9a-f]+\s+sz=\d+$")


def family_of(group):
    """The family of a mesh group: everything in front of the "@"."""
    i = group.find("_@")
    return group[:i] if i > 0 else group


def variant_of(group):
    """The name the model gave to the mesh group: everything behind the "#&"."""
    i = group.find("_#&")
    return group[i + 3:] if i > 0 else ""


def is_locked(family):
    f = family.lower()
    if f in LOCKED_EXACT:
        return True
    return any(f.startswith(p) for p in LOCKED_PREFIXES)


# ---------------------------------------------------------------- extract


def parse_data_block(text):
    """Reads m_meshGroups and m_nDefaultMeshGroupMask out of a DATA block."""
    groups = None
    default_mask = None
    in_groups = False
    for line in text.splitlines():
        s = line.strip()
        if in_groups:
            if s.startswith("]"):
                in_groups = False
                continue
            s = s.rstrip(",")
            if len(s) >= 2 and s.startswith('"') and s.endswith('"'):
                groups.append(s[1:-1].replace('\\"', '"'))
            continue
        if s.startswith("m_meshGroups"):
            groups = []
            in_groups = True
        elif s.startswith("m_nDefaultMeshGroupMask"):
            try:
                default_mask = int(s.split("=", 1)[1].strip())
            except ValueError:
                default_mask = None
    return groups or [], default_mask


def cli_list_models(vpk):
    out = subprocess.run([CLI, "-i", vpk, "--vpk_dir"], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    models = []
    for line in out.splitlines():
        m = LIST_ENTRY.match(line.strip())
        if not m:
            continue
        path = m.group(1).replace("\\", "/")
        if path.startswith("characters/models/") and path.endswith(".vmdl_c"):
            models.append(path)
    return sorted(models)


def cli_read_data(vpk, path):
    out = subprocess.run([CLI, "-i", vpk, "-b", "DATA", "-f", path], capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    return parse_data_block(out)


def cmd_extract(args):
    models = cli_list_models(args.vpk)
    print("models in %s: %d" % (args.vpk, len(models)))
    raw = {}
    for i, path in enumerate(models, 1):
        groups, default = cli_read_data(args.vpk, path)
        key = path[: -len(".vmdl_c")] + ".vmdl"
        raw[key] = {"groups": groups, "default": default}
        if i % 25 == 0:
            print("  %d/%d" % (i, len(models)))
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(raw, f, ensure_ascii=False, indent=1)
    print("wrote %s (%d models)" % (args.out, len(raw)))


# ------------------------------------------------------------------ build


def build_categories(groups, default_mask):
    """Groups the mesh groups by family. Every family is kept, the ones that
    the player may not change are marked as closed."""
    families = []
    by_family = {}
    for index, group in enumerate(groups):
        family = family_of(group)
        if family not in by_family:
            by_family[family] = []
            families.append(family)
        by_family[family].append((index, group))

    categories = []
    for family in families:
        members = by_family[family]
        locked = is_locked(family)
        items = []
        for index, group in members:
            if len(members) == 1:
                label = variant_of(group) or family
            else:
                label = variant_of(group) or group
            items.append({
                "label": label,
                "index": index,
                "group": group,
                "enabled": not locked,
            })
        categories.append({
            "name": family,
            "type": "opradio" if len(members) > 1 else "multi",
            "items": items,
        })
    return categories


def cmd_build(args):
    with open(args.raw, "r", encoding="utf-8-sig") as f:
        raw = json.load(f)

    out = {}
    skipped = []
    for path, info in raw.items():
        groups = info.get("groups") or []
        default_mask = info.get("default")
        if not groups or default_mask is None:
            # Without the default mask of the model there is no way to tell
            # which of the closed groups the model wants, so it is left alone.
            skipped.append(path)
            continue
        categories = build_categories(groups, default_mask)
        out[path] = {
            "default": default_mask,
            "categories": categories,
        }

    with open(args.out, "w", encoding="utf-8") as f:
        # The catalogue is only read by the plugin, the file the server owner
        # edits is written from it. It is written without the indentation to
        # keep the plugin small.
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"), sort_keys=True)

    groups = sum(len(c["items"]) for m in out.values() for c in m["categories"])
    closed = sum(1 for m in out.values() for c in m["categories"] for i in c["items"] if not i["enabled"])
    print("wrote %s" % args.out)
    print("  models: %d" % len(out))
    print("  mesh groups: %d (%d of them closed by default)" % (groups, closed))
    print("  skipped (no default mask): %d" % len(skipped))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_extract = sub.add_parser("extract", help="read the DATA blocks of a VPK")
    p_extract.add_argument("--vpk", required=True, help="the _dir.vpk of the addon")
    p_extract.add_argument("--out", required=True, help="the raw json to write")
    p_extract.set_defaults(func=cmd_extract)

    p_build = sub.add_parser("build", help="turn the raw data into the catalogue")
    p_build.add_argument("--raw", required=True, help="the raw json")
    p_build.add_argument("--out", required=True, help="the catalogue json to write")
    p_build.set_defaults(func=cmd_build)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
