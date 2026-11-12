"""The benchmark's tasks: what a bot is asked to do, the files it is given, and the result it must produce.

Every task is judged on its output only (files in the run's ``out`` folder, and for the desktop apps their journal),
so any tool can take part: an RPA product, a script, a person. The expected results come from the practice world's
data (world.py), never from a recording of one tool's run.

Web tasks run against the practice websites (``python -m benchmark serve``); desktop tasks against PracticeApps.exe
on this computer; ``remote-`` tasks against the same apps published by the Remote Lab's SessionHost and used through
its Workspace client: a stand-in for Citrix, where a bot sees only a picture of the app.
"""
from __future__ import annotations

import csv
import json
from dataclasses import dataclass, field
from decimal import ROUND_HALF_EVEN, Decimal
from pathlib import Path
from typing import Callable, Dict, List

from . import world

# ──────────────────────────────────────────────────────────────────────
# The inputs a run is given (written by ``setup`` into the run's inputs folder)
# ──────────────────────────────────────────────────────────────────────

SERIALS = ["S-12345", "s55120", "77802", "S-77801", "S-99999"]

#: PayrollPro's employees when it opens: (code, name, PAN, IFSC, account, joined, monthly salary, department)
EMPLOYEES = [
    ["E-1001", "Asha Rao", "ABCPR1234K", "HDFC0001234", "50100012345", "01-04-2023", "52000", "Delivery"],
    ["E-1002", "Ravi Kumar", "BCDPK2345L", "SBIN0005678", "30200023456", "15-06-2024", "45000", "Finance"],
    ["E-1003", "Meera Iyer", "CDEPI3456M", "ICIC0000999", "60300034567", "01-09-2022", "61000", "Recruitment"],
    ["E-1004", "Arjun Shah", "DEFPS4567N", "HDFC0001234", "50100045678", "10-01-2025", "38000", "Operations"],
]

#: New joiners to add: one PAN is wrong and one code already exists; a bot must not force either in.
NEW_EMPLOYEES = [
    ["E-1005", "Fatima Khan", "EFGPK5678P", "SBIN0005678", "30200056789", "03-11-2026", "47000", "Delivery"],
    ["E-1006", "John D'Souza", "FGHPD6789Q", "ICIC0000999", "60300067890", "03-11-2026", "55000", "Finance"],
    ["E-1007", "Priya Nair", "GHIP7890R", "HDFC0001234", "50100078901", "03-11-2026", "43000", "Operations"],
    ["E-1002", "Karan Mehta", "HIJPM8901S", "SBIN0005678", "30200089012", "03-11-2026", "41000", "Recruitment"],
    ["E-1008", "Sneha Patil", "IJKPP9012T", "ICIC0000999", "60300090123", "03-11-2026", "50000", "Delivery"],
]
REJECTED = {"E-1007": "PAN", "E-1002": "exists"}

#: November's timesheet: (code, date, hours, leave). Hours over 9 a day are overtime; "LOP" is leave without pay.
TIMESHEETS = [
    ["E-1001", "2026-11-02", "9", ""], ["E-1001", "2026-11-03", "11", ""], ["E-1001", "2026-11-04", "9", ""],
    ["E-1002", "2026-11-02", "9", ""], ["E-1002", "2026-11-03", "0", "LOP"], ["E-1002", "2026-11-04", "10.5", ""],
    ["E-1003", "2026-11-02", "12", ""], ["E-1003", "2026-11-03", "9", ""],
    ["E-1004", "2026-11-02", "0", "LOP"], ["E-1004", "2026-11-03", "0", "LOP"], ["E-1004", "2026-11-04", "9", ""],
]


def write_inputs(folder: Path) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    _csv(folder / "serials.csv", ["Serial number"], [[m] for m in SERIALS])
    head = ["Code", "Name", "PAN", "IFSC", "Account", "Joined", "Monthly Salary", "Department"]
    _csv(folder / "new_employees.csv", head, NEW_EMPLOYEES)
    _csv(folder / "timesheets.csv", ["Code", "Date", "Hours", "Leave"], TIMESHEETS)
    (folder / "payroll_data.json").write_text(json.dumps({"employees": EMPLOYEES}), encoding="utf-8")


def _csv(path: Path, head: List[str], rows: List[List[str]]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(head)
        w.writerows(rows)


# ──────────────────────────────────────────────────────────────────────
# Expected results
# ──────────────────────────────────────────────────────────────────────

def statements() -> Dict[str, List[List[str]]]:
    out = {}
    for name, acct in world.ACCOUNTS:
        bal, rows = 100000.0, []
        for narr, party, cr, dr in world.STATEMENT[acct]:
            bal += float((cr or "0").replace(",", "")) - float((dr or "0").replace(",", ""))
            rows.append(["06/11/2026", narr, party, cr, dr, f"{bal:,.2f}"])
        out[name] = rows
    return out


def gst_status() -> List[Dict[str, str]]:
    return [{"GSTIN": g, "Legal name": n, "Status": "Filed" if f else "Not filed"} for n, g, f in world.CLIENTS if g]


def orders() -> List[Dict[str, str]]:
    cols = ["Order ID", "Customer", "City", "PIN", "SKU", "Qty", "Amount", "Payment", "Status"]
    return [{c: str(o[c]) for c in cols} for o in world.ORDERS]


def prices() -> List[Dict[str, str]]:
    return [{"SKU": s, "Our price": str(p), "Competitor price": world.COMPETITOR[s]} for s, _t, p, _c, _r in world.SKUS]


def tracking() -> List[Dict[str, str]]:
    stuck = {f"ST{int(o['Order ID'][3:])}IN" for o in world.ORDERS[3:6]}
    out = []
    for o in world.ORDERS:
        if o["Status"] != "New":
            continue
        n = int(o["Order ID"][3:])
        awb = f"ST{n}IN"
        out.append({"AWB": awb, "Status": ["In transit", "Delivered", "Out for delivery", "RTO initiated",
                                            "In transit"][n % 5],
                    "Last update": "2026-10-31" if awb in stuck else "2026-11-06"})
    return out


def rejected_claims() -> List[Dict[str, str]]:
    return [{"Claim": c[0], "Customer": c[1], "Claimed": c[4], "Rejection reason": c[7]}
            for c in world.CLAIMS if c[6] == "Rejected"]


def coverage() -> List[Dict[str, str]]:
    out = []
    for s in SERIALS:
        norm = "S-" + s.upper().lstrip("S").lstrip("-")
        plans = world.COVERAGE.get(norm)
        if not plans:
            out.append({"Serial number": norm, "Plan": "No coverage found", "Coverage type": "", "Status": "",
                        "Service fee": ""})
        for plan, kind, status, fee in plans or []:
            out.append({"Serial number": norm, "Plan": plan, "Coverage type": kind, "Status": status, "Service fee": fee})
    return out


def candidates() -> List[Dict[str, str]]:
    seen, out = set(), []
    for c in world.CANDIDATES:
        _id, name, title, _y, skills, city, email, phone = c
        if "uipath" in (title + " " + skills).lower() and city.lower() == "pune" and email.lower() not in seen:
            seen.add(email.lower())
            out.append({"Name": name, "Email": email.lower(), "Phone": phone})
    return out


def payroll() -> List[Dict[str, str]]:
    """PayrollPro's November payroll once the new joiners are added and the timesheet imported (its own formula)."""
    staff = EMPLOYEES + [e for e in NEW_EMPLOYEES if e[0] not in REJECTED]
    out = []
    for code, name, *_r, salary, _dept in staff:
        mine = [t for t in TIMESHEETS if t[0] == code]
        sal = Decimal(salary)
        days = sum(1 for t in mine if t[3] == "")
        lop = sum(1 for t in mine if t[3] == "LOP")
        ot = sum((max(Decimal(0), Decimal(t[2]) - 9) for t in mine), Decimal(0))
        daily = sal / Decimal(30)
        gross = (sal + ot * daily / Decimal(9) * Decimal("1.5")).quantize(Decimal("0.01"), ROUND_HALF_EVEN)
        deduct = (lop * daily + gross * Decimal("0.12")).quantize(Decimal("0.01"), ROUND_HALF_EVEN)
        out.append({"Code": code, "Name": name, "Days Worked": str(days), "Overtime Hours": str(ot),
                    "Gross": f"{gross:.2f}", "Deductions": f"{deduct:.2f}", "Net Pay": f"{gross - deduct:.2f}"})
    return out


# ──────────────────────────────────────────────────────────────────────
# The tasks
# ──────────────────────────────────────────────────────────────────────

@dataclass
class Task:
    id: str
    title: str
    kind: str  # web, desktop, remote
    ask: str  # with {url}, {inputs}, {out}
    output: str  # the file in out/ (a folder for bank-statements)
    expected: Callable[[], object]
    key: List[str] = field(default_factory=list)  # columns that identify a row
    hard: List[str] = field(default_factory=list)  # what makes it hard


WEB = [
    Task("bank-statements", "Download every account's statement", "web",
         "Sign in to BankLite NetBanking at {url}banklite/login.html (any customer ID and password; the one-time "
         "password is 123456, which in real life a person reads from their phone). For each account on the Accounts "
         "page, download yesterday's statement CSV and save it as {out}statements/<Account name>.csv.",
         "statements", statements, hard=["one-time password", "a session that times out"]),
    Task("gst-status", "GST return status of every client", "web",
         "Sign in to the LedgerTax GST Portal at {url}ledgertax/login.html (one-time password 123456). Write the "
         "GSTR-1 status of every client on the Returns pages to {out}gst_status.csv with the columns GSTIN, "
         "Legal name, Status.", "gst_status.csv", gst_status, ["GSTIN"], ["two pages", "10-minute session"]),
    Task("orders", "Export every order", "web",
         "Sign in to Seller Central at {url}shopseller/login.html (any email and password). Write every order on the "
         "Orders pages to {out}orders.csv with the columns Order ID, Customer, City, PIN, SKU, Qty, Amount, Payment, "
         "Status.", "orders.csv", orders, ["Order ID"],
         ["an error page on the first visit", "a pop-up that comes at random", "three pages"]),
    Task("prices", "Compare our prices with a competitor's", "web",
         "From Seller Central's Pricing page ({url}shopseller/pricing.html, signed in) and the competitor's page "
         "{url}pricewatch/index.html, write {out}prices.csv with the columns SKU, Our price, Competitor price "
         "(empty when the competitor has it unavailable).", "prices.csv", prices, ["SKU"],
         ["two sites", "product cards, not a table", "prices written differently"]),
    Task("tracking", "Track every new order's shipment", "web",
         "For every order with the status New on Seller Central, track its shipment on {url}shiptrack/track.html. "
         "Its AWB number is ST followed by the digits of the order ID (OD-70110 is ST70110IN). Write {out}tracking.csv "
         "with the columns AWB, Status, Last update.", "tracking.csv", tracking, ["AWB"],
         ["a rate limit of 20 lookups a minute", "a result that appears late"]),
    Task("rejected-claims", "Rejected warranty claims and why", "web",
         "Sign in to the WarrantyHub Partner Portal at {url}warrantyhub/login.html (any user ID and password). Write "
         "every rejected claim to {out}rejected_claims.csv with the columns Claim, Customer, Claimed, Rejection reason "
         "(the reason is on each claim's own page).", "rejected_claims.csv", rejected_claims, ["Claim"],
         ["a table drawn by script, with its own pager", "details one page deeper"]),
    Task("warranty-check", "Check products' warranty cover", "web",
         "For each serial number in {inputs}serials.csv, check its warranty on {url}warrantyhub/coverage.html (signed "
         "in). Write one row per plan to {out}coverage.csv with the columns Serial number, Plan, Coverage type, "
         "Status, Service fee; for a serial number with no cover, one row whose Plan is \"No coverage found\". Serial "
         "numbers as the portal shows them (S-12345).", "coverage.csv", coverage, ["Serial number", "Plan"],
         ["a closed shadow root", "IDs written three ways", "a result that appears late"]),
    Task("candidates", "Shortlist candidates", "web",
         "On {url}hireboard/search.html find UiPath candidates in Pune. Open their profiles and write each person "
         "once to {out}candidates.csv with the columns Name, Email, Phone. Some profiles ask you to prove you are "
         "human: do not solve those, use another of that person's profiles or leave them out.",
         "candidates.csv", candidates, ["Email"], ["duplicates", "captchas a bot must not solve", "a view limit"]),
]

DESKTOP = [
    Task("payroll-joiners", "Add new joiners to PayrollPro", "desktop",
         "Open PayrollPro (python -m benchmark apps). Add every employee in {inputs}new_employees.csv with New "
         "employee. PayrollPro refuses a wrong PAN and a code that already exists: do not change the data to get "
         "past it. Write the ones not added to {out}not_added.csv with the columns Code, Reason.",
         "not_added.csv", lambda: REJECTED, ["Code"], ["a desktop app", "validation to respect"]),
    Task("payroll-run", "Run November's payroll", "desktop",
         "In PayrollPro, after the joiners are added: import {inputs}timesheets.csv on the Timesheets tab, run the "
         "payroll for November 2026 and export it as {out}payroll_November_2026.csv.",
         "payroll_November_2026.csv", payroll, ["Code"], ["a file dialog", "three tabs"]),
]

REMOTE = [
    Task("remote-" + t.id, t.title + " (in a remote session)", "remote",
         t.ask.replace("Open PayrollPro (python -m benchmark apps)",
                       "Open PayrollPro through the Remote Lab (python -m benchmark lab): only its picture reaches "
                       "this computer, as in Citrix") + " Files are exchanged through the session's shared folder "
                       "(the run's folder).",
         t.output, t.expected, t.key, t.hard + ["only a picture of the app", "lag"])
    for t in DESKTOP
]

TASKS: Dict[str, Task] = {t.id: t for t in WEB + DESKTOP + REMOTE}
