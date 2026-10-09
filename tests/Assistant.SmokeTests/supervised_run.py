"""Offline primitives for private supervised runners; never connects or sends.

Adapters must hold SessionLock through child exit and cleanup, persist spends before
dispatch, and keep receipts outside repositories. This is not a complete executor.
"""
import hashlib
import json
import math
import os
import time
from pathlib import Path


class Refused(RuntimeError):
    pass


class SessionLock:
    def __init__(self, path):
        self.path = Path(path)
        self.file = None

    def __enter__(self):
        if self.file is not None:
            raise Refused("Session lock is already held.")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        stream = self.path.open("a+b")
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            # Windows byte locks also cover bytes beyond EOF.
            lock = lambda: msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            lock = lambda: fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            lock()
        except OSError:
            stream.close()
            raise Refused("Session lock is busy.") from None
        self.file = stream
        return self

    def require_held(self):
        if self.file is None or self.file.closed:
            raise Refused("Session lock must be held.")

    def __exit__(self, *_):
        if self.file is not None:
            # Closing the handle releases the OS lock; never unlink its path.
            self.file.close()
            self.file = None


class Ledger:
    def __init__(self, path, lock, clock=time.time):
        self.path, self.lock, self.clock = Path(path), lock, clock
        self.batch = None

    def create(self, run_id, deadline, cap):
        self.lock.require_held()
        if (not isinstance(run_id, str) or not run_id or type(cap) is not int or cap <= 0
                or not isinstance(deadline, (float, int)) or not math.isfinite(deadline)
                or deadline <= self.clock()):
            raise Refused("Invalid run limits.")
        state = {"run_id": run_id, "deadline": deadline, "cap": cap, "operations": []}
        # Exclusive creation cannot replace a spent or expired run.
        with self.path.open("x", encoding="utf-8") as stream:
            json.dump(state, stream, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        return self.digest()

    def read(self):
        self.lock.require_held()
        state = json.loads(self.path.read_text(encoding="utf-8"))
        if (set(state) != {"run_id", "deadline", "cap", "operations"}
                or not isinstance(state["run_id"], str) or not state["run_id"]
                or not isinstance(state["deadline"], (float, int))
                or not math.isfinite(state["deadline"])
                or type(state["cap"]) is not int or state["cap"] <= 0
                or not isinstance(state["operations"], list)
                or len(state["operations"]) > state["cap"]):
            raise Refused("Invalid ledger.")
        labels = []
        for operation in state["operations"]:
            if (set(operation) != {"label", "outcome", "source", "cleaned"}
                    or operation["outcome"] not in {"unknown", "pass", "failed"}
                    or type(operation["cleaned"]) is not bool
                    or (operation["source"] is not None
                        and (not isinstance(operation["source"], str) or not operation["source"]))
                    or not isinstance(operation["label"], str) or not operation["label"]):
                raise Refused("Invalid operation.")
            labels.append(operation["label"])
        if len(labels) != len(set(labels)):
            raise Refused("Duplicate ledger labels.")
        return state

    def digest(self):
        self.lock.require_held()
        return hashlib.sha256(self.path.read_bytes()).hexdigest()

    def resume(self, expected_digest, labels):
        if self.digest() != expected_digest:
            raise Refused("Ledger checkpoint changed.")
        state = self.read()
        self._ready(state)
        if (not 1 <= len(labels) <= 2 or len(set(labels)) != len(labels)
                or any(not isinstance(label, str) or not label for label in labels)
                or set(labels) & {op["label"] for op in state["operations"]}
                or len(state["operations"]) + len(labels) > state["cap"]):
            raise Refused("Batch is replayed, invalid or over budget.")
        self.batch = tuple(labels)
        return self.batch

    def _ready(self, state):
        # Reserve cleanup time; continuation never moves the original deadline.
        if self.clock() >= state["deadline"] - 5:
            raise Refused("Original run deadline reached.")
        if any(op["outcome"] != "pass" or not op["source"] or not op["cleaned"]
               for op in state["operations"]):
            raise Refused("Prior charged operation is unresolved.")

    def spend(self, label):
        state = self.read()
        self._ready(state)
        if (not isinstance(label, str) or not label
                or (self.batch is not None and label not in self.batch)
                or len(state["operations"]) >= state["cap"]
                or label in {op["label"] for op in state["operations"]}):
            raise Refused("Send is replayed or over budget.")
        state["operations"].append(
            {"label": label, "outcome": "unknown", "source": None, "cleaned": False})
        self._save(state)  # Caller dispatches only after this returns.
        if self.batch is not None:
            self.batch = tuple(item for item in self.batch if item != label)

    def resolve(self, label, outcome, source, cleaned):
        state = self.read()
        if (outcome not in {"pass", "failed"} or not isinstance(source, str) or not source
                or type(cleaned) is not bool):
            raise Refused("Invalid outcome evidence.")
        operation = next((op for op in state["operations"] if op["label"] == label), None)
        if operation is None or operation["outcome"] != "unknown":
            raise Refused("Operation is absent or already resolved.")
        operation.update(outcome=outcome, source=source, cleaned=cleaned)
        self._save(state)

    def _save(self, state):
        self.lock.require_held()
        temporary = self.path.with_suffix(self.path.suffix + ".tmp")
        with temporary.open("w", encoding="utf-8") as stream:
            json.dump(state, stream, sort_keys=True)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, self.path)


class Cleanup:
    """One supervisor owns cleanup; retain lock if joining children fails."""
    def __init__(self):
        self.started = False

    def run(self, join_children, stop_owned_resources):
        if self.started:
            raise Refused("Cleanup already entered.")
        self.started = True
        join_children()
        stop_owned_resources()


class HistoryCadence:
    def __init__(self, clock=time.monotonic, sleep=time.sleep):
        self.clock, self.sleep, self.next_read = clock, sleep, 0.0

    def wait(self):
        self.sleep(max(0, self.next_read - self.clock()))
        self.next_read = self.clock() + 3.0


def capture_then_parse(path, raw, parse):
    """Keep the exact raw receipt privately even when parsing fails."""
    with Path(path).open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    return parse(raw)
