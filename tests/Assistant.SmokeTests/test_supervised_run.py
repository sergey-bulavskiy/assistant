import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from supervised_run import Cleanup, HistoryCadence, Ledger, Refused, SessionLock, capture_then_parse


class SupervisedRunTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lock = SessionLock(self.root / "session.lock")
        self.lock.__enter__()
        self.addCleanup(self.lock.__exit__)
        self.now = 100.0
        self.ledger = Ledger(self.root / "ledger.json", self.lock, lambda: self.now)
        self.ledger.create("synthetic-run", 200.0, 3)

    def pass_case(self, label):
        self.ledger.spend(label)
        self.ledger.resolve(label, "pass", "synthetic-source-" + label, True)

    def test_creation_requires_live_lock_and_cannot_replace_ledger(self):
        before = self.ledger.path.read_bytes()
        with self.assertRaises(FileExistsError):
            self.ledger.create("replacement", 300, 20)
        self.lock.__exit__()
        with self.assertRaises(Refused):
            self.ledger.create("replacement", 300, 20)
        self.assertEqual(before, self.ledger.path.read_bytes())

    def test_os_lock_excludes_other_process_without_losing_ownership(self):
        script = "from supervised_run import SessionLock; import sys; SessionLock(sys.argv[1]).__enter__()"
        result = subprocess.run([sys.executable, "-B", "-c", script, str(self.lock.path)],
                                cwd=Path(__file__).parent, capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Session lock is busy", result.stderr)
        self.ledger.spend("still-owned")
        self.assertEqual("still-owned", self.ledger.read()["operations"][0]["label"])

    def test_resume_spent_prefix_preserves_deadline_and_charge_before_dispatch(self):
        self.pass_case("first")
        self.pass_case("second")
        previous = self.ledger.read()
        self.assertEqual(("third",), self.ledger.resume(self.ledger.digest(), ["third"]))
        with self.assertRaises(Refused):
            self.ledger.spend("unselected")
        self.ledger.spend("third")
        continued = self.ledger.read()
        self.assertEqual(previous["operations"], continued["operations"][:2])
        self.assertEqual(200.0, continued["deadline"])
        self.assertEqual({"label": "third", "outcome": "unknown", "source": None,
                          "cleaned": False}, continued["operations"][2])
        self.ledger.resolve("third", "pass", "synthetic-source-third", True)
        with self.assertRaises(Refused):
            self.ledger.spend("fourth")
        self.assertEqual(3, len(self.ledger.read()["operations"]))

    def test_resume_rejects_changed_checkpoint_replay_and_oversized_batch(self):
        checkpoint = self.ledger.digest()
        self.pass_case("first")
        for digest, labels in [(checkpoint, ["next"]), (self.ledger.digest(), ["first"]),
                               (self.ledger.digest(), ["a", "b", "c"]),
                               (self.ledger.digest(), ["same", "same"])]:
            with self.subTest(labels=labels), self.assertRaises(Refused):
                self.ledger.resume(digest, labels)
        self.assertEqual(1, len(self.ledger.read()["operations"]))

    def test_expired_continuation_never_renews_original_deadline(self):
        self.now = 194.999
        self.assertEqual(("next",), self.ledger.resume(self.ledger.digest(), ["next"]))
        for now in [195.0, 200.0, 300.0]:
            self.now = now
            with self.subTest(now=now), self.assertRaises(Refused):
                self.ledger.resume(self.ledger.digest(), ["next"])
        self.assertEqual(200.0, self.ledger.read()["deadline"])
        self.assertEqual([], self.ledger.read()["operations"])

    def test_invalid_deadline_and_corrupt_operation_fail_closed(self):
        with self.assertRaises(Refused):
            Ledger(self.root / "new.json", self.lock).create("synthetic", float("nan"), 3)
        self.assertFalse((self.root / "new.json").exists())
        state = self.ledger.read()
        state["operations"] = [{"label": "first", "outcome": "pass", "source": {}, "cleaned": True}]
        self.ledger.path.write_text(json.dumps(state), encoding="utf-8")
        with self.assertRaises(Refused):
            self.ledger.spend("second")

    def test_release_allows_next_process_to_acquire_same_lock(self):
        self.lock.__exit__()
        script = "from supervised_run import SessionLock; import sys;\nwith SessionLock(sys.argv[1]): print('acquired')"
        result = subprocess.run([sys.executable, "-B", "-c", script, str(self.lock.path)],
                                cwd=Path(__file__).parent, capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(b"acquired", result.stdout.strip())

    def test_resolved_operation_cannot_be_overwritten(self):
        self.pass_case("first")
        before = self.ledger.path.read_bytes()
        with self.assertRaises(Refused):
            self.ledger.resolve("first", "failed", "another-source", True)
        self.assertEqual(before, self.ledger.path.read_bytes())

    def test_unknown_failed_and_missing_cleanup_block_new_sends(self):
        self.ledger.spend("first")
        for outcome, source, cleaned in [("unknown", None, False),
                                         ("failed", "synthetic-source", True),
                                         ("pass", "synthetic-source", False)]:
            state = self.ledger.read()
            state["operations"][0].update(outcome=outcome, source=source, cleaned=cleaned)
            self.ledger.path.write_text(json.dumps(state), encoding="utf-8")
            with self.subTest(outcome=outcome), self.assertRaises(Refused):
                self.ledger.spend("second")
            self.assertEqual(1, len(self.ledger.read()["operations"]))

    def test_cleanup_joins_before_stopping_and_cannot_run_twice(self):
        calls = []
        cleanup = Cleanup()
        cleanup.run(lambda: calls.append("join"), lambda: calls.append("stop"))
        with self.assertRaises(Refused):
            cleanup.run(lambda: calls.append("join"), lambda: calls.append("stop"))
        self.assertEqual(["join", "stop"], calls)

    def test_failed_child_join_never_stops_resources(self):
        calls = []
        def fail():
            raise TimeoutError("synthetic child still active")
        with self.assertRaises(TimeoutError):
            Cleanup().run(fail, lambda: calls.append("stop"))
        self.assertEqual([], calls)
        self.lock.require_held()

    def test_raw_receipt_survives_parser_failure_and_cannot_be_overwritten(self):
        path = self.root / "receipt.bin"
        def parse(raw):
            self.assertEqual(raw, path.read_bytes())
            raise ValueError("synthetic malformed response")
        raw = b'{"synthetic":"raw response"}'
        with self.assertRaises(ValueError):
            capture_then_parse(path, raw, parse)
        with self.assertRaises(FileExistsError):
            capture_then_parse(path, b"replacement", lambda _: None)
        self.assertEqual(raw, path.read_bytes())

    def test_history_cadence_is_shared_across_immediate_reads(self):
        clock = [10.0]
        reads = []
        def sleep(delay):
            clock[0] += delay
        cadence = HistoryCadence(lambda: clock[0], sleep)
        for _ in range(3):
            cadence.wait()
            reads.append(clock[0])
        self.assertEqual([10.0, 13.0, 16.0], reads)


if __name__ == "__main__":
    unittest.main()
