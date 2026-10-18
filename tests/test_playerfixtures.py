"""Privacy checks for the offline public-fixture exporter (synthetic source data)."""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))
from make_player_fixture import public_body
from playerprobe import parse_identity


class PublicFixtureTests(unittest.TestCase):
    def identity(self):
        prefix = b"\x01\0\0\0\0\x07\x07SecretX\x0b\0\0\0\x02\x01"
        private = b"PrivateGuildAndProfileIdentifiers" * 3
        suffix = bytes(11) + bytes.fromhex("21f4cb1fec03000013000000049900000000000000")
        return prefix + private + suffix

    def test_identity_minimises_private_middle_and_preserves_field_offsets(self):
        original = self.identity()
        result = public_body("4536", original, "PlayerA")
        record, why = parse_identity("4536", result)
        self.assertIsNone(why)
        self.assertEqual(record["name"], "PlayerA")
        self.assertEqual(len(result), len(original))
        self.assertEqual(result[-32:], original[-32:])
        self.assertNotIn(b"SecretX", result)
        self.assertNotIn(b"PrivateGuild", result)
        self.assertEqual(set(result[record["nameEndOffset"] + 6:-32]), {0})

    def test_alias_must_preserve_utf8_byte_length(self):
        for alias in (None, "Short", "BadNameTooLong", "1234567", "Bad!XXX"):
            with self.subTest(alias=alias), self.assertRaises(ValueError):
                public_body("4536", self.identity(), alias)

    def test_missing_private_middle_is_rejected(self):
        with self.assertRaises(ValueError):
            public_body("4536", self.identity()[:30], "PlayerA")

    def test_kill_record_drops_killer_name_and_other_identifiers(self):
        original = b"\x01\x11\x22\x33\x44KillerPrivateName"
        result = public_body("048d", original)
        self.assertEqual(result[:5], original[:5])
        self.assertEqual(len(result), len(original))
        self.assertEqual(set(result[5:]), {0})

    def test_movement_bytes_stay_unchanged(self):
        original = bytes(range(30))
        self.assertEqual(public_body("1a37", original), original)


if __name__ == "__main__":
    unittest.main()
