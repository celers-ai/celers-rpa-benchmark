"""Judging a run: each task's output against its expected result (tasks.py).

Values are compared the way a person checking the file would: spaces and letter case do not matter, "₹1,200",
"1200" and "1200.00" are the same amount, a phone number is its digits, an email is lower case. A row is right only
when every expected column is right; extra columns are ignored. A task passes when every expected row is there, right,
and nothing else is.
"""
from __future__ import annotations

import csv
import json
import re
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Dict, List, Optional

from .tasks import REJECTED, TASKS, Task

_MONEY = re.compile(r"(?i)rs\.?|inr|₹|\$|,|/-|\s")


def norm(column: str, value: object) -> str:
    text = re.sub(r"\s+", " ", str(value if value is not None else "")).strip()
    col = column.lower()
    if col == "phone":
        return re.sub(r"\D", "", text)[-10:]
    if col == "email":
        return text.lower()
    plain = _MONEY.sub("", text)
    if plain and re.fullmatch(r"-?\d+(\.\d+)?", plain):
        try:
            return format(Decimal(plain).normalize(), "f")
        except InvalidOperation:
            pass
    return text.lower()


def read_rows(path: Path) -> List[Dict[str, str]]:
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return [{(k or "").strip(): (v or "") for k, v in row.items()} for row in csv.DictReader(fh)]


def _pick(row: Dict[str, str], column: str) -> Optional[str]:
    for k, v in row.items():
        if k.lower() == column.lower():
            return v
    return None


def compare(task: Task, got: List[Dict[str, str]], expected: List[Dict[str, str]]) -> Dict[str, object]:
    columns = list(expected[0]) if expected else []
    missing_columns = [c for c in columns if got and _pick(got[0], c) is None]
    if missing_columns:
        return {"pass": False, "why": f"columns missing: {', '.join(missing_columns)}", "right": 0,
                "expected": len(expected)}

    def key(row):
        return tuple(norm(c, _pick(row, c)) for c in task.key)

    want = {key(r): r for r in expected}
    have: Dict[tuple, Dict[str, str]] = {}
    duplicates = 0
    for r in got:
        k = key(r)
        duplicates += k in have
        have[k] = r
    right, wrong = 0, []
    for k, r in want.items():
        g = have.get(k)
        if g is None:
            continue
        bad = [c for c in columns if norm(c, _pick(g, c)) != norm(c, r[c])]
        if bad:
            wrong.append({"row": " / ".join(k), "columns": bad})
        else:
            right += 1
    missing = [" / ".join(k) for k in want if k not in have]
    extra = [" / ".join(k) for k in have if k not in want]
    ok = right == len(want) and not extra and not duplicates
    why = "" if ok else "; ".join(filter(None, [
        f"{len(missing)} rows missing ({', '.join(missing[:3])}{'…' if len(missing) > 3 else ''})" if missing else "",
        f"{len(wrong)} rows with wrong values ({wrong[0]['row']}: {', '.join(wrong[0]['columns'])})" if wrong else "",
        f"{len(extra)} rows that should not be there ({', '.join(extra[:3])})" if extra else "",
        f"{duplicates} rows twice" if duplicates else ""]))
    return {"pass": ok, "why": why, "right": right, "expected": len(want)}


def _statements(task: Task, out: Path) -> Dict[str, object]:
    expected = task.expected()
    folder = out / task.output
    right, notes = 0, []
    cols = ["Txn Date", "Narration", "Party", "Credit", "Debit", "Balance"]
    for name, rows in expected.items():
        path = folder / f"{name}.csv"
        if not path.is_file():
            notes.append(f"{name}.csv missing")
            continue
        got = [[norm(c, _pick(r, c)) for c in cols] for r in read_rows(path)]
        want = [[norm(c, v) for c, v in zip(cols, r)] for r in rows]
        if got == want:
            right += 1
        else:
            notes.append(f"{name}.csv differs")
    return {"pass": right == len(expected), "why": "; ".join(notes), "right": right, "expected": len(expected)}


def _journal(path: Path) -> List[dict]:
    if not path.is_file():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def _joiners(task: Task, out: Path, journal: Path) -> Dict[str, object]:
    from .tasks import NEW_EMPLOYEES

    events = [e for e in _journal(journal) if e.get("app") == "payroll"]
    added = {e["code"].upper(): e for e in events if e.get("event") == "employee_added"}
    notes = []
    want = {e[0]: e for e in NEW_EMPLOYEES if e[0] not in REJECTED}
    right = 0
    for code, e in want.items():
        got = added.get(code)
        if got is None:
            notes.append(f"{code} not added")
        elif (norm("name", got.get("name")), norm("pan", got.get("pan")), norm("ifsc", got.get("ifsc")),
              norm("salary", got.get("salary")), norm("department", got.get("department"))) != \
                (norm("name", e[1]), norm("pan", e[2]), norm("ifsc", e[3]), norm("salary", e[6]), norm("department", e[7])):
            notes.append(f"{code} added with other values")
        else:
            right += 1
    forced = [c for c in added if c not in want]
    if forced:
        notes.append(f"added what should have been refused: {', '.join(forced)}")
    path = out / task.output
    reported = {norm("code", _pick(r, "Code")).upper(): norm("reason", _pick(r, "Reason") or "")
                for r in (read_rows(path) if path.is_file() else [])}
    for code, why in REJECTED.items():
        reason = reported.get(code)
        words = ("pan",) if why == "PAN" else ("exist", "duplicate", "already")
        if reason is None:
            notes.append(f"{code} not in {task.output}")
        elif not any(w in reason for w in words):
            notes.append(f"{code}'s reason does not say why ({why})")
        else:
            right += 1
    if set(reported) - set(REJECTED):
        notes.append(f"{task.output} lists ones that were added: {', '.join(sorted(set(reported) - set(REJECTED)))}")
    total = len(want) + len(REJECTED)
    return {"pass": right == total and not forced and not set(reported) - set(REJECTED), "why": "; ".join(notes),
            "right": right, "expected": total}


def judge_task(task: Task, run: Path) -> Dict[str, object]:
    out = run / "out"
    journal = run / ("remote_journal.jsonl" if task.kind == "remote" else "journal.jsonl")
    if task.id.endswith("bank-statements"):
        return _statements(task, out)
    if task.id.endswith("payroll-joiners"):
        return _joiners(task, out, journal)
    path = out / task.output
    if not path.is_file():
        return {"pass": False, "why": f"{task.output} not written", "right": 0, "expected": len(task.expected())}
    return compare(task, read_rows(path), task.expected())


def judge(run: Path, only: Optional[List[str]] = None) -> Dict[str, object]:
    run = Path(run)
    meta = json.loads((run / "run.json").read_text(encoding="utf-8")) if (run / "run.json").is_file() else {}
    results = {}
    for tid, task in TASKS.items():
        if only and tid not in only:
            continue
        results[tid] = judge_task(task, run)
    passed = sum(1 for r in results.values() if r["pass"])
    report = {"night": meta.get("night"), "passed": passed, "tasks": len(results), "results": results}
    (run / "results.json").write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
    return report
