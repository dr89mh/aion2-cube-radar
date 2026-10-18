"""Named identity records (wire opcodes 45 36 PlayerInfo, 33 36 SelfInfo) from a capture, for offline diagnosis.

Only this layout is checked: entity id (varint), mask1 (four bytes), mask2 (one byte, bit 0 must be set), u8 name
length in UTF-8 bytes (1..36), the name. Faction and rank are NOT decoded and never guessed: every record says
"unknown" and carries the full body plus the 32 bytes right after the name, so they can be studied by hand. Markers
from a markers.jsonl next to the input are attached when they lie within 10 s of a record with a known time.

Reads files only: no network, no live capture. Raw captures go through a2wire (server_streams + Decoder); a top-level
frame gets the arrival time of the segment holding its first byte, frames from bundles keep t = null.

usage: python tools/playerprobe.py <raw.pcapng|.pcap|frames.jsonl> --out probe.json [--name NAME] [--limit 100]
"""
import argparse
import bisect
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from a2wire import Decoder, load_jsonl, server_streams, varint  # noqa: E402

OPCODES = ("4536", "3336")  # PlayerInfo, SelfInfo (config/opcodes.json)
MASK_BYTES = 4
MAX_NAME_BYTES = 36
AFTER_NAME = 32             # bytes dumped right after the name
MARKER_WINDOW_MS = 10_000
UNKNOWN = "unknown"         # faction / rank: not decoded
LAYOUT = "id varint | mask1 u32 | mask2 u8 (bit 0 set) | u8 name length (1..36 UTF-8 bytes) | name | ..."


def parse_identity(op, body):
    """(record, None) for a named identity record, else (None, reason)."""
    op = op.lower()
    if op not in OPCODES:
        return None, "unsupported_opcode"
    r = varint(body, 0)
    if r is None:
        return None, "truncated" if len(body) < 5 else "bad_id"
    eid, o = r
    if not 0 < eid <= 0xFFFFFFFF:
        return None, "bad_id"
    if o + MASK_BYTES + 2 > len(body):
        return None, "truncated"
    o += MASK_BYTES
    if not body[o] & 1:
        return None, "mask2_bit0_clear"
    o += 1
    n = body[o]
    o += 1
    if n == 0:
        return None, "empty_name"
    if n > MAX_NAME_BYTES:
        return None, "name_too_long"
    if o + n > len(body):
        return None, "truncated"
    try:
        name = body[o:o + n].decode("utf-8")
    except UnicodeDecodeError:
        return None, "invalid_utf8"
    if not name.strip() or not name.isprintable():
        return None, "invalid_name"
    end = o + n
    return {"opcode": op, "entityId": eid, "name": name, "nameEndOffset": end, "bodyHex": body.hex(),
            "afterNameHex": body[end:end + AFTER_NAME].hex(), "faction": UNKNOWN, "rank": UNKNOWN}, None


def source_kind(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in (".pcap", ".pcapng"):
        return "pcap"
    if ext == ".jsonl":
        return "jsonl"
    raise ValueError(f"{path}: expected .pcapng, .pcap or frames .jsonl")


def markers_path(capture):
    return os.path.join(os.path.dirname(os.path.abspath(capture)), "markers.jsonl")


def frame_time(times, offs, offset):
    """Unix ms of the segment that delivered stream byte `offset`; None when unknown (nested frame, no timestamp)."""
    if offset < 0:
        return None
    i = bisect.bisect_right(offs, offset) - 1
    if i < 0 or not times[i][1]:
        return None
    return times[i][1] // 1000


def jsonl_frames(path):
    """(t ms | None, op, body, src, path) per frame line."""
    for r in load_jsonl(path, ("frame",)):
        t = r.get("t")
        yield (t if isinstance(t, int) else None), r["op"], bytes.fromhex(r["hex"]), r.get("src"), r.get("path", "")


def capture_frames(path, stats):
    """Same from a raw capture, every server->client stream."""
    for st in server_streams(path).values():
        stats["streams"] += 1
        stats["gaps"] += st.gaps
        offs = [off for off, _ in st.times]
        for f in Decoder().decode_stream(bytes(st.data), aligned=st.syn_seen):
            yield frame_time(st.times, offs, f.offset), f.op, f.body, f.src, f.path


def nearby(markers, t):
    """Markers within the window around t; None when that cannot be told (no marker file or no time)."""
    if markers is None or t is None:
        return None
    return [{"t": m["t"], "dtMs": m["t"] - t, "label": m.get("label", ""), "text": m.get("text"), "via": m.get("via")}
            for m in markers if abs(m["t"] - t) <= MARKER_WINDOW_MS]


def probe(capture, name=None, limit=100):
    kind = source_kind(capture)
    mpath = markers_path(capture)
    markers = None
    if os.path.isfile(mpath):
        markers = [m for m in load_jsonl(mpath, ("marker",)) if isinstance(m.get("t"), (int, float))]
    stats = {"framesScanned": 0, "identityFrames": 0, "named": 0, "rejected": {}, "nameMatched": 0, "emitted": 0,
             "limit": limit, "truncated": False, "timeUnknown": 0}
    if kind == "pcap":
        stats.update(streams=0, gaps=0)
    frames = capture_frames(capture, stats) if kind == "pcap" else jsonl_frames(capture)
    want = None if name is None else name.casefold()
    records = []
    for t, op, body, src, fpath in frames:
        stats["framesScanned"] += 1
        if op.lower() not in OPCODES:
            continue
        stats["identityFrames"] += 1
        rec, why = parse_identity(op, body)
        if rec is None:
            stats["rejected"][why] = stats["rejected"].get(why, 0) + 1
            continue
        stats["named"] += 1
        if want is not None and rec["name"].casefold() != want:
            continue
        stats["nameMatched"] += 1
        if len(records) < limit:
            records.append({"t": t, **rec, "src": src, "framePath": fpath, "markers": nearby(markers, t)})
    stats["emitted"] = len(records)
    stats["truncated"] = stats["nameMatched"] > len(records)
    stats["timeUnknown"] = sum(r["t"] is None for r in records)  # of the emitted records
    return {"source": capture, "kind": kind, "markersSource": mpath if markers is not None else None,
            "markerWindowMs": MARKER_WINDOW_MS, "nameFilter": name, "opcodes": list(OPCODES), "layout": LAYOUT,
            "faction": UNKNOWN, "rank": UNKNOWN, "summary": stats, "records": records}


def main(argv=None):
    ap = argparse.ArgumentParser(description="Named identity records (45 36 / 33 36) from a capture, offline.")
    ap.add_argument("capture", help="raw.pcapng / .pcap or frames.jsonl")
    ap.add_argument("--name", help="only this name (exact, case-insensitive)")
    ap.add_argument("--out", required=True, help="output JSON file")
    ap.add_argument("--limit", type=int, default=100, help="max records written (default 100)")
    a = ap.parse_args(argv)
    if a.limit < 0:
        ap.error("--limit must be >= 0")
    try:
        source_kind(a.capture)
    except ValueError as e:
        ap.error(str(e))
    inputs = {os.path.normcase(os.path.abspath(p)) for p in (a.capture, markers_path(a.capture))}
    if os.path.normcase(os.path.abspath(a.out)) in inputs:
        ap.error("--out would overwrite an input file")

    result = probe(a.capture, a.name, a.limit)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1, ensure_ascii=False)
        f.write("\n")
    s = result["summary"]
    print(f"{a.out}: {s['emitted']} of {s['nameMatched']} matching records written"
          + (f" (truncated at --limit {a.limit})" if s["truncated"] else "")
          + f"; {s['named']} named of {s['identityFrames']} identity frames, {s['framesScanned']} frames scanned; "
          f"rejected {s['rejected'] or 'none'}; time unknown {s['timeUnknown']}")


if __name__ == "__main__":
    main()
