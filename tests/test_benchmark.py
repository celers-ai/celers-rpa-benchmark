"""The judge passes perfect outputs and fails wrong ones; a run folder is made as TASKS.md says."""
import csv
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchmark import tasks  # noqa: E402
from benchmark.__main__ import main  # noqa: E402
from benchmark.judge import judge  # noqa: E402


def _write(path: Path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)


def _perfect(run: Path, journal: str = "journal.jsonl"):
    out = run / "out"
    for name, rows in tasks.statements().items():
        _write(out / "statements" / f"{name}.csv",
               [dict(zip(["Txn Date", "Narration", "Party", "Credit", "Debit", "Balance"], r)) for r in rows])
    for t in tasks.WEB + tasks.DESKTOP:
        if t.id in ("bank-statements", "payroll-joiners"):
            continue
        _write(out / t.output, t.expected())
    _write(out / "not_added.csv", [{"Code": "E-1007", "Reason": "PAN must be 5 letters, 4 digits and a letter"},
                                   {"Code": "E-1002", "Reason": "Employee code exists"}])
    lines = [{"app": "payroll", "event": "employee_added", "code": e[0], "name": e[1], "pan": e[2], "ifsc": e[3],
              "salary": e[6], "department": e[7]} for e in tasks.NEW_EMPLOYEES if e[0] not in tasks.REJECTED]
    for name in ("journal.jsonl", "remote_journal.jsonl"):
        (run / name).write_text("\n".join(json.dumps(x) for x in lines), encoding="utf-8")


def test_setup_makes_a_run_folder(tmp_path):
    assert main(["setup", "--run", str(tmp_path), "--night", "3"]) == 0
    meta = json.loads((tmp_path / "run.json").read_text(encoding="utf-8"))
    assert meta["night"] == 3 and meta["changes"]
    assert (tmp_path / "world" / "banklite" / "login.html").is_file() and (tmp_path / "world" / "_drift.js").is_file()
    assert (tmp_path / "inputs" / "serials.csv").is_file()
    text = (tmp_path / "TASKS.md").read_text(encoding="utf-8")
    assert "## orders" in text and "http://127.0.0.1:8780/shopseller/login.html" in text and "{url}" not in text


def test_perfect_outputs_pass_every_task(tmp_path):
    _perfect(tmp_path)
    report = judge(tmp_path)
    failed = {k: v["why"] for k, v in report["results"].items() if not v["pass"]}
    assert not failed and report["passed"] == len(tasks.TASKS) == 12


def test_values_written_another_way_still_pass(tmp_path):
    _perfect(tmp_path)
    rows = tasks.orders()
    for r in rows:
        r["Amount"] = r["Amount"].replace("₹", "Rs. ") + ".00"
        r["Customer"] = r["Customer"].upper()
    _write(tmp_path / "out" / "orders.csv", rows)
    cands = [dict(c, Phone="0" + "".join(ch for ch in c["Phone"] if ch.isdigit())[-10:], Email=c["Email"].upper())
             for c in tasks.candidates()]
    _write(tmp_path / "out" / "candidates.csv", cands)
    assert judge(tmp_path, ["orders", "candidates"])["passed"] == 2


def test_missing_wrong_and_extra_rows_fail(tmp_path):
    _perfect(tmp_path)
    rows = tasks.orders()
    _write(tmp_path / "out" / "orders.csv", rows[:-1])
    track = tasks.tracking()
    track[0]["Status"] = "Delivered" if track[0]["Status"] != "Delivered" else "In transit"
    _write(tmp_path / "out" / "tracking.csv", track)
    _write(tmp_path / "out" / "gst_status.csv", tasks.gst_status() + [{"GSTIN": "X", "Legal name": "Y", "Status": "Filed"}])
    r = judge(tmp_path)["results"]
    assert not r["orders"]["pass"] and "1 rows missing" in r["orders"]["why"]
    assert not r["tracking"]["pass"] and "wrong values" in r["tracking"]["why"]
    assert not r["gst-status"]["pass"] and "should not be there" in r["gst-status"]["why"]


def test_forcing_a_refused_employee_in_fails(tmp_path):
    _perfect(tmp_path)
    forced = {"app": "payroll", "event": "employee_added", "code": "E-1007", "name": "Priya Nair",
              "pan": "GHIPN7890R", "ifsc": "HDFC0001234", "salary": "43000", "department": "Operations"}
    with open(tmp_path / "journal.jsonl", "a", encoding="utf-8") as fh:
        fh.write("\n" + json.dumps(forced))
    r = judge(tmp_path, ["payroll-joiners"])["results"]["payroll-joiners"]
    assert not r["pass"] and "should have been refused: E-1007" in r["why"]


def test_the_payroll_is_paid_by_the_apps_own_formula():
    pay = {r["Code"]: r for r in tasks.payroll()}
    assert len(pay) == 7 and "E-1007" not in pay and "E-1005" in pay
    # E-1001: 52000, 2 hours over: 52000 + 2 * (52000/30)/9 * 1.5 = 52577.78; 12 % = 6309.33
    assert pay["E-1001"]["Gross"] == "52577.78" and pay["E-1001"]["Deductions"] == "6309.33"
    assert pay["E-1004"]["Days Worked"] == "1"  # two days of leave without pay
