"""Export minimised, pseudonymised Global fixtures, offline.

--names must point to a PRIVATE JSON map kept outside git (e.g. captures/fixture-names.private.json):
{"OriginalName": {"alias": "AliasSameLen", "faction": "Asmodian", "rankCode": 19,
                  "rankName": "Trainee", "markerMs": 123, "captures": ["capture-folder"]}}
Aliases must have the same UTF-8 byte length. Identity payloads retain only the prefix and last 32 bytes;
the private, undecoded middle is zero-filled. Kill records retain only victim id/u32 and are otherwise zero-filled.
"""
import argparse
import json
from pathlib import Path

from a2wire import load_jsonl, varint
from playerprobe import parse_identity


def public_body(opcode, body, alias=None):
    if opcode == "4536":
        record, why = parse_identity(opcode, body)
        if record is None:
            raise ValueError(f"Invalid identity: {why}")
        original = record["name"].encode("utf-8")
        if not isinstance(alias, str):
            raise ValueError("A public alias is required")
        replacement = alias.encode("utf-8")
        if len(replacement) != len(original) or not alias.isalnum() or not any(c.isalpha() for c in alias):
            raise ValueError("Alias must be alphanumeric and preserve the UTF-8 byte length")
        end = record["nameEndOffset"]
        keep = end + 6  # pcId, race and gender; never publish the unparsed profile/equipment/IDs after these
        if keep + 32 > len(body):
            raise ValueError("Identity too short to retain disjoint prefix and suffix")
        prefix = body[:end - len(original)] + replacement + body[end:keep]
        return prefix + bytes(len(body) - keep - 32) + body[-32:]
    if opcode == "048d":
        result = varint(body, 0)
        if result is None or result[1] + 4 > len(body):
            raise ValueError("Invalid kill record")
        end = result[1] + 4
        return body[:end] + bytes(len(body) - end)
    return body


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--names", required=True, type=Path, help="private name/alias/ground-truth JSON map")
    parser.add_argument("--frames", action="append", type=Path, help="decoded JSONL input; repeat for multiple captures")
    parser.add_argument("--out", type=Path, default=root / "fixtures/players/2026-10-18_abyss.json")
    args = parser.parse_args()
    output = args.out
    if output.exists():
        raise SystemExit(f"Refusing to overwrite {output}")
    labels = json.loads(args.names.read_text(encoding="utf-8"))
    selected = []
    sources = args.frames or [root / "captures" / folder / "frames.jsonl"
                              for folder in ("20261018_201957", "20261018_203833")]
    for source in sources:
        folder = source.parent.name
        try:
            public_source = str(source.resolve().relative_to(root))
        except ValueError:
            public_source = source.name # do not leak a private absolute path in public fixtures
        identities = {}
        identity_counts = {}
        movement = set()
        for f in load_jsonl(str(source), ("frame",)):
            if f.get("type") != "frame":
                continue
            b = bytes.fromhex(f["hex"])
            keep = False
            name = None
            if f["op"] == "4536":
                record, _ = parse_identity("4536", b)
                if record and record["name"] in labels:
                    name = record["name"]
                    if folder not in labels[name].get("captures", [folder]):
                        continue
                    eid = record["entityId"]
                    count = identity_counts.get(eid, 0)
                    keep = count < labels[name].get("maxIdentitiesPerCapture", 1)
                    identity_counts[eid] = count + 1
                    identities[eid] = name
            elif f["op"] in ("1a37", "1b37", "1c37", "2937", "4736", "048d"):
                result = varint(b, 0)
                if result and result[0] in identities:
                    name = identities[result[0]]
                    key = (result[0], f["op"])
                    keep = key not in movement
                    movement.add(key)
            if keep:
                alias = labels[name]["alias"]
                selected.append({"source": public_source, "t": f["t"], "op": f["op"],
                                 "name": alias, "hex": public_body(f["op"], b, alias).hex()})
    public_labels = {v["alias"]: {k: v.get(k) for k in ("faction", "rankCode", "rankName", "markerMs")}
                     for v in labels.values()}
    for value in labels.values():
        if value.get("screenshot"):
            public_labels[value["alias"]].update(screenshot=Path(value["screenshot"]).name,
                                                screenshotMs=value.get("screenshotMs"))
    result = {"note": "Derived from real Global records. Names pseudonymised with byte lengths preserved. Identity "
                      "prefix/last32 bytes retained, private middle zero-filled; kill records retain victim id/u32 only. "
                      "Only marker-labelled enemies have screenshot corroboration; other metadata are observations.",
              "anonymized": True, "labels": public_labels, "frames": selected}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"{output}: {len(selected)} minimised records")


if __name__ == "__main__":
    main()
