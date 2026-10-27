import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from compare_ryusync_diagnostics import compare, load


class CompareTests(unittest.TestCase):
    def fixtures(self):
        meta = dict(session="same", build="same", role="Host", total="2", error="")
        row = (True, ((1, 0, 0, 0, 0), (2, 0, 0, 0, 0)), (0, 1, 257, 2, 0, 0), (0, 2, 257, 3, 0, 0))
        return (meta, {10: row, 11: row}), ({**meta, "role": "Guest"}, {10: row, 11: row})

    def test_equal_is_not_a_determinism_claim(self):
        text = compare(*self.fixtures())
        self.assertIn("does NOT establish matching fighter state", text)
        self.assertIn("Earlier updates are not covered", text)

    def test_difference_after_application(self):
        a, b = self.fixtures()
        row = b[1][11]
        b[1][11] = (*row[:3], (0, 9, 257, 3, 0, 0))
        text = compare(a, b)
        self.assertIn("update 11", text)
        self.assertIn("after.counter04", text)

    def test_missing_and_invalid_inputs(self):
        a, b = self.fixtures()
        a[1][13] = b[1][13] = a[1][11]
        row = b[1][10]
        b[1][10] = (False, (), *row[2:])
        text = compare(a, b)
        self.assertIn("inside overlap: 1", text)
        self.assertIn("unknown input metadata: 1", text)
        self.assertNotIn("applied controller inputs differ", text)

    def test_wrong_session_role_or_no_overlap(self):
        for change in ("session", "role", "overlap"):
            a, b = self.fixtures()
            if change == "session": b[0]["session"] = "different"
            if change == "role": b[0]["role"] = "Host"
            if change == "overlap": b[1].clear()
            with self.assertRaises(ValueError): compare(a, b)

    def test_parser_rejects_duplicate_steps(self):
        header = "RYUSYNC_DIAGNOSTICS_V1\nsession=x\nrole=Host\nbuild=b\ntotal=1\nerror=\n"
        row = "0|1|1,0,0,0,0|2,0,0,0,0|0,1,101,2,3F800000,0|0,2,101,3,3F800000,0\n"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.txt"
            path.write_text(header + row, encoding="utf-8")
            self.assertEqual(load(path)[1][0][2][4], 0x3F800000)
            path.write_text(header + row + row, encoding="utf-8")
            with self.assertRaises(ValueError): load(path)


if __name__ == "__main__":
    unittest.main()
