"""Tests for tools/playerprobe.py (stdlib unittest, synthetic data and real identity prefixes).

run: python -m unittest discover -s tests -p "test_*.py"
"""
import contextlib
import io
import json
import os
import socket
import struct
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))
import playerprobe  # noqa: E402
from playerprobe import parse_identity  # noqa: E402

TAIL = bytes(range(1, 41))  # no 0xFF: two of those would look like a bundle to the embedded scan
T0 = 1_760_000_000_000


def leb(v):
    out = bytearray()
    while True:
        b, v = v & 0x7F, v >> 7
        out.append(b | 0x80 if v else b)
        if not v:
            return bytes(out)


def body(eid=4242, name="Testchar", mask=b"\x00\x00\x00\x00", mask2=1, raw_name=None, tail=TAIL):
    n = name.encode("utf-8") if raw_name is None else raw_name
    return leb(eid) + mask + bytes([mask2, len(n)]) + n + tail


def run(*argv):
    """main() with any socket use turned into a failure; returns stdout."""
    out = io.StringIO()
    boom = AssertionError("network access")
    with mock.patch.object(socket, "socket", side_effect=boom), \
            mock.patch.object(socket, "create_connection", side_effect=boom), contextlib.redirect_stdout(out):
        playerprobe.main(list(argv))
    return out.getvalue()


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


class ParseIdentityTests(unittest.TestCase):
    def test_valid_named_record(self):
        b = body(4242, "Testchar")
        rec, why = parse_identity("4536", b)
        self.assertIsNone(why)
        self.assertEqual(rec["opcode"], "4536")
        self.assertEqual(rec["entityId"], 4242)
        self.assertEqual(rec["name"], "Testchar")
        self.assertEqual(rec["nameEndOffset"], 2 + 4 + 1 + 1 + 8)
        self.assertEqual(rec["bodyHex"], b.hex())
        self.assertEqual(rec["afterNameHex"], TAIL[:32].hex())
        self.assertEqual((rec["faction"], rec["rank"]), ("unknown", "unknown"))

    def test_selfinfo_multibyte_name_and_short_tail(self):
        rec, why = parse_identity("3336", body(7, "아이온", tail=b"\x01\x02"))
        self.assertIsNone(why)
        self.assertEqual(rec["name"], "아이온")
        self.assertEqual(rec["nameEndOffset"], 1 + 4 + 1 + 1 + 9)  # length counts UTF-8 bytes
        self.assertEqual(rec["afterNameHex"], "0102")

    def test_name_length_limit(self):
        self.assertIsNone(parse_identity("4536", body(name="a" * 36))[1])
        self.assertEqual(parse_identity("4536", body(raw_name=b"a" * 37))[1], "name_too_long")

    def test_truncated(self):
        b = body(4242, "Testchar")  # id 2 bytes, mask1 2..5, mask2 6, length 7, name 8..15
        for cut in (0, 1, 3, 6, 7, 10):
            with self.subTest(cut=cut):
                self.assertEqual(parse_identity("4536", b[:cut]), (None, "truncated"))
        self.assertEqual(parse_identity("4536", b"\x80" * 5 + b)[1], "bad_id")

    def test_invalid_utf8(self):
        self.assertEqual(parse_identity("4536", body(raw_name=b"\xff\xfe"))[1], "invalid_utf8")
        self.assertEqual(parse_identity("4536", body(raw_name="아".encode("utf-8")[:2]))[1], "invalid_utf8")

    def test_unsupported_opcode(self):
        for op in ("4136", "0036", "3436"):
            with self.subTest(op=op):
                self.assertEqual(parse_identity(op, body()), (None, "unsupported_opcode"))

    def test_mask2_bit0_required(self):
        self.assertEqual(parse_identity("4536", body(mask=b"\xff\xff\xff\xff", mask2=2))[1], "mask2_bit0_clear")
        self.assertIsNone(parse_identity("4536", body(mask=b"\x00\x00\x00\x00", mask2=3))[1])

    def test_invalid_entity_id(self):
        for eid in (0, 0x100000000):
            with self.subTest(eid=eid):
                self.assertEqual(parse_identity("4536", body(eid=eid))[1], "bad_id")

    def test_real_global_identity_prefixes(self):
        fixture = os.path.join(os.path.dirname(os.path.dirname(__file__)), "fixtures", "players",
                               "2026-10-15_global_identity_prefixes.json")
        for f in load(fixture)["frames"]:
            with self.subTest(op=f["op"]):
                rec, why = parse_identity(f["op"], bytes.fromhex(f["hex"]))
                self.assertIsNone(why)
                for key in ("entityId", "name", "nameEndOffset"):
                    self.assertEqual(rec[key], f[key])
                self.assertEqual((rec["faction"], rec["rank"]), ("unknown", "unknown"))

    def test_empty_or_unprintable_name(self):
        self.assertEqual(parse_identity("4536", body(raw_name=b""))[1], "empty_name")
        self.assertEqual(parse_identity("4536", body(name=" "))[1], "invalid_name")
        self.assertEqual(parse_identity("4536", body(name="a\x00b"))[1], "invalid_name")


class JsonlEndToEndTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.dir = tmp.name
        self.frames = os.path.join(self.dir, "frames.jsonl")
        self.markers = os.path.join(self.dir, "markers.jsonl")
        self.out = os.path.join(self.dir, "probe.json")
        rows = [
            (T0, "0036", b"\x00" * 4, {}),
            (T0 + 100, "4536", body(1001, "Alpha"), {}),
            (T0 + 200, "4536", body(1002, "Alphabet"), {}),
            (T0 + 300, "3336", body(1003, "alpha"), {}),
            (T0 + 400, "4536", body(1004, raw_name=b"\xff\xfe"), {}),
            (T0 + 500, "4536", body(1005, "Gamma")[:9], {}),
            (T0 + 600, "4136", body(1006, "Delta"), {}),
            (T0 + 60_000, "4536", body(1007, "Alpha"), {"src": "lz4", "depth": 1, "path": "B1.0"}),
        ]
        with open(self.frames, "w", encoding="utf-8") as f:
            f.write(json.dumps({"type": "session", "t": T0, "mode": "replay", "source": "x", "tool": "test"}) + "\n")
            for i, (t, op, b, extra) in enumerate(rows):
                r = {"type": "frame", "n": i + 1, "t": t, "flow": 1, "src": "top", "depth": 0, "op": op,
                     "len": len(b), "wire": len(b) + 3, "hex": b.hex(), **extra}
                f.write(json.dumps(r) + "\n")
        with open(self.markers, "w", encoding="utf-8") as f:
            f.write(json.dumps({"type": "marker", "t": T0 + 2_000, "label": "player seen", "text": "Alpha nearby",
                                "via": "hotkey"}) + "\n")
            f.write(json.dumps({"type": "marker", "t": T0 + 30_000, "label": "far", "via": "console"}) + "\n")

    def test_all_named_records(self):
        run(self.frames, "--out", self.out)
        res = load(self.out)
        self.assertEqual(res["source"], self.frames)
        self.assertEqual(res["kind"], "jsonl")
        self.assertEqual(os.path.normcase(res["markersSource"]), os.path.normcase(os.path.abspath(self.markers)))
        self.assertEqual((res["faction"], res["rank"]), ("unknown", "unknown"))
        s = res["summary"]
        self.assertEqual((s["framesScanned"], s["identityFrames"], s["named"]), (8, 6, 4))
        self.assertEqual(s["rejected"], {"invalid_utf8": 1, "truncated": 1})
        self.assertEqual((s["nameMatched"], s["emitted"], s["truncated"], s["timeUnknown"]), (4, 4, False, 0))
        recs = res["records"]
        self.assertEqual([r["name"] for r in recs], ["Alpha", "Alphabet", "alpha", "Alpha"])
        self.assertEqual([r["opcode"] for r in recs], ["4536", "4536", "3336", "4536"])
        self.assertTrue(all(r["faction"] == "unknown" and r["rank"] == "unknown" for r in recs))
        first = recs[0]
        self.assertEqual(first["t"], T0 + 100)
        self.assertEqual(first["bodyHex"], body(1001, "Alpha").hex())
        self.assertEqual(first["markers"], [{"t": T0 + 2_000, "dtMs": 1_900, "label": "player seen",
                                             "text": "Alpha nearby", "via": "hotkey"}])
        last = recs[-1]
        self.assertEqual(last["markers"], [])  # both markers further than 10 s away
        self.assertEqual((last["src"], last["framePath"]), ("lz4", "B1.0"))

    def test_name_filter_exact_case_insensitive(self):
        run(self.frames, "--out", self.out, "--name", "ALPHA")
        res = load(self.out)
        self.assertEqual([r["entityId"] for r in res["records"]], [1001, 1003, 1007])
        self.assertEqual(res["nameFilter"], "ALPHA")

        run(self.frames, "--out", self.out, "--name", "alph")
        s = load(self.out)["summary"]
        self.assertEqual((s["named"], s["nameMatched"], s["emitted"], s["truncated"]), (4, 0, 0, False))

    def test_limit_truncates(self):
        printed = run(self.frames, "--out", self.out, "--name", "Alpha", "--limit", "2")
        res = load(self.out)
        self.assertEqual([r["entityId"] for r in res["records"]], [1001, 1003])
        s = res["summary"]
        self.assertEqual((s["nameMatched"], s["emitted"], s["limit"], s["truncated"]), (3, 2, 2, True))
        self.assertIn("truncated at --limit 2", printed)

    def test_without_markers_file(self):
        os.remove(self.markers)
        run(self.frames, "--out", self.out)
        res = load(self.out)
        self.assertIsNone(res["markersSource"])
        self.assertTrue(all(r["markers"] is None for r in res["records"]))

    def test_refuses_to_overwrite_input(self):
        with open(self.frames, "rb") as f:
            before = f.read()
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            run(self.frames, "--out", self.frames)
        with open(self.frames, "rb") as f:
            self.assertEqual(f.read(), before)


# ---------------------------------------------------------------- raw capture

def frame(op, b):
    return leb(len(b) + 6) + op + b  # varint counts opcode + body + 4


def bundle(inner):
    lit = len(inner) - 15  # LZ4 block with literals only
    ext = b"\xff" * (lit // 255) + bytes([lit % 255])
    payload = b"\xff\xff" + struct.pack("<I", len(inner)) + b"\xf0" + ext + inner
    return leb(len(payload) + 4) + payload


def tcp_packet(seq, flags, payload):
    tcp = struct.pack(">HHIIBBHHH", 13328, 50000, seq, 0, 5 << 4, flags, 65535, 0, 0) + payload
    ip = struct.pack(">BBHHHBBH4s4s", 0x45, 0, 20 + len(tcp), 0, 0, 64, 6, 0, bytes([192, 0, 2, 1]),
                     bytes([192, 0, 2, 2])) + tcp
    return b"\x00" * 12 + b"\x08\x00" + ip


def pcap(packets):
    out = struct.pack("<IHHiIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 1)
    for t_us, p in packets:
        out += struct.pack("<IIII", t_us // 1_000_000, t_us % 1_000_000, len(p), len(p)) + p
    return out


class CaptureEndToEndTests(unittest.TestCase):
    def test_times_from_segments_nested_frames_stay_null(self):
        with tempfile.TemporaryDirectory() as d:
            t1 = T0 * 1000 + 250_000
            t2 = t1 + 1_500_000
            seg1 = frame(b"\x45\x36", body(2001, "Alpha"))
            seg2 = frame(b"\x45\x36", body(2002, "Beta")) + bundle(frame(b"\x33\x36", body(2003, "Gamma")))
            cap = os.path.join(d, "raw.pcap")
            with open(cap, "wb") as f:
                f.write(pcap([(t1 - 1_000, tcp_packet(1000, 0x12, b"")),
                              (t1, tcp_packet(1001, 0x18, seg1)),
                              (t2, tcp_packet(1001 + len(seg1), 0x18, seg2))]))
            with open(os.path.join(d, "markers.jsonl"), "w", encoding="utf-8") as f:
                f.write(json.dumps({"type": "marker", "t": t2 // 1000 + 500, "label": "m1", "via": "hotkey"}) + "\n")
            out = os.path.join(d, "probe.json")
            run(cap, "--out", out)
            res = load(out)

        self.assertEqual((res["source"], res["kind"]), (cap, "pcap"))
        s = res["summary"]
        self.assertEqual((s["streams"], s["gaps"], s["framesScanned"], s["named"]), (1, 0, 3, 3))
        self.assertEqual(s["timeUnknown"], 1)
        recs = res["records"]
        self.assertEqual([(r["name"], r["t"], r["src"]) for r in recs],
                         [("Alpha", t1 // 1000, "top"), ("Beta", t2 // 1000, "top"), ("Gamma", None, "lz4")])
        self.assertEqual([m["dtMs"] for m in recs[0]["markers"]], [2_000])
        self.assertEqual([m["dtMs"] for m in recs[1]["markers"]], [500])
        self.assertIsNone(recs[2]["markers"])  # no time, so no marker matching
        self.assertEqual(recs[2]["framePath"], "B1.0")


if __name__ == "__main__":
    unittest.main()
