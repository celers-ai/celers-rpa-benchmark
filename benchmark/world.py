"""The practice world of the Celers RPA Benchmark: fourteen made-up business websites, served from this computer.

No third-party site, no real account, no money. Every page is static HTML with its behaviour in JavaScript (sessions
in localStorage, as a site's cookies keep them; counters there too), served by the benchmark's own server, so a run can be repeated exactly.
The difficulties the plan names are built in: sign-in with an OTP the person types, a captcha the bot must not
solve, paged tables, a pop-up that comes at random, an error page on the first visit, a rate limit, slow downloads,
shadow DOM, an iframe form, confirm dialogs, a chart drawn on a canvas.

``build(root)`` writes every site under ``root`` and returns the data the sites show (``DATA``), from which the
expected outputs are worked out (tasks.py).
"""
from __future__ import annotations

import csv
import html
import json
import random
from pathlib import Path
from typing import Any, Dict, List

SEED = 20261107

STYLE = """<style>
body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f4f6f8;color:#1f2933}
header{background:#1d3557;color:#fff;padding:10px 18px;display:flex;gap:18px;align-items:center}
header a{color:#dbe7ff;text-decoration:none} main{padding:18px;max-width:1100px}
table{border-collapse:collapse;background:#fff;width:100%} th,td{border:1px solid #d5dbe1;padding:6px 8px;text-align:left}
th{background:#e9eef3} .btn{background:#2563eb;color:#fff;border:0;padding:7px 14px;border-radius:4px;cursor:pointer}
label{display:block;margin-top:8px} input,select,textarea{padding:5px;min-width:240px}
.modal{position:fixed;inset:0;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center}
.modal>div{background:#fff;padding:22px;border-radius:8px;max-width:420px}
.err{color:#b42318} .ok{color:#067647} .pager a{margin:0 6px}
</style>"""


def page(title: str, body: str, nav: str = "", script: str = "") -> str:
    return (f"<!doctype html><html lang='en'><head><meta charset='utf-8'><title>{html.escape(title)}</title>{STYLE}"
            f"</head><body>{('<header>' + nav + '</header>') if nav else ''}<main>{body}</main>"
            f"{('<script>' + script + '</script>') if script else ''}</body></html>")


def _write(root: Path, rel: str, text: str) -> None:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


def _table(cols: List[str], rows: List[List[Any]], tid: str = "data") -> str:
    head = "".join(f"<th>{html.escape(c)}</th>" for c in cols)
    body = "".join("<tr>" + "".join(f"<td>{html.escape(str(v))}</td>" for v in r) + "</tr>" for r in rows)
    return f"<table id='{tid}'><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>"


#: A session kept in localStorage (a sign-in lasts, as a cookie does): a page behind sign-in sends the visitor to sign in, and back after.
GUARD = """(function(){var k='%(key)s';var s=localStorage.getItem(k);var t=+localStorage.getItem(k+'_t')||0;
if(!s||Date.now()-t>%(ms)d){localStorage.removeItem(k);location.href='%(login)s?next='+encodeURIComponent(location.pathname.split('/').pop()+location.search);throw new Error('sign in first');}
else{localStorage.setItem(k+'_t',Date.now());}})();"""


def _guard(key: str, login: str, minutes: float = 30) -> str:
    return GUARD % {"key": key, "ms": int(minutes * 60000), "login": login}


def _signin(site: str, key: str, title: str, user_label: str, otp: bool, home: str) -> Dict[str, str]:
    """A sign-in page (and an OTP page when ``otp``): the OTP is the person's (sent to their phone)."""
    after = "otp.html" if otp else home
    login = page(f"{title}: Sign in", f"""<h1>{html.escape(title)}</h1>
<form id='signin' onsubmit='return go()'><label for='u'>{user_label}</label><input id='u' name='user' autocomplete='username'>
<label for='p'>Password</label><input id='p' name='password' type='password'>
<p><button class='btn' type='submit'>Sign in</button></p><p id='msg' class='err'></p></form>""",
                 script=f"""if(localStorage.getItem('{key}')&&!new URLSearchParams(location.search).get('next')){{location.replace('{home}');}}
function go(){{var u=document.getElementById('u').value,p=document.getElementById('p').value;
if(!u||!p){{document.getElementById('msg').textContent='Enter your user ID and password.';return false;}}
var n=new URLSearchParams(location.search).get('next')||'{home}';
{"localStorage.setItem('" + key + "_pending','1');location.href='otp.html?next='+encodeURIComponent(n);" if otp else
  "localStorage.setItem('" + key + "','" + site + "');localStorage.setItem('" + key + "_t',Date.now());location.href=n;"}
return false;}}""")
    pages = {"login.html": login}
    if otp:
        pages["otp.html"] = page(f"{title}: One-time password", f"""<h1>Verify it's you</h1>
<p>We sent a 6-digit one-time password to your registered mobile number ending 4471. It is valid for 5 minutes.</p>
<form onsubmit='return check()'><label for='otp'>One-time password</label><input id='otp' name='otp' inputmode='numeric' maxlength='6'>
<p><button class='btn' type='submit'>Verify</button></p><p id='msg' class='err'></p></form>""",
                                 script=f"""function check(){{var v=document.getElementById('otp').value.trim();
if(!/^\\d{{6}}$/.test(v)){{document.getElementById('msg').textContent='Enter the 6-digit OTP.';return false;}}
localStorage.setItem('{key}','{site}');localStorage.setItem('{key}_t',Date.now());
location.href=new URLSearchParams(location.search).get('next')||'{home}';return false;}}""")
    return pages


# ──────────────────────────────────────────────────────────────────────
# The data every site shows (deterministic)
# ──────────────────────────────────────────────────────────────────────

CLIENTS = [  # Sharma & Co.'s clients: (name, GSTIN, filed last month)
    ("Sharma Traders", "27AABCS1429B1Z5", True), ("Gupta & Sons", "27AAFFG7781K1Z2", False),
    ("Mehta Steel", "27AACCM4410Q1ZT", True), ("Rao Agencies", "29AAKFR5531H1Z9", False),
    ("Kulkarni Foods", "27AAGCK9921L1Z1", True), ("Iyer Textiles", "33AAACI2288M1Z0", True),
    ("Bose Electricals", "19AADCB6614N1ZX", False), ("Desai Pharma", "24AAECD3317P1Z6", True),
    ("Nair Logistics", "32AAHFN8823R1Z4", True), ("Patel Plastics", "24AAJCP5520S1Z8", False),
    ("Reddy Motors", "36AAKCR1188T1Z2", True), ("Singh Hardware", "03AALFS7764U1Z7", True),
    ("Khan Exports", "09AAMCK4459V1Z3", False), ("Joshi Prints", "27AANFJ2201W1Z9", True),
    ("Verma Dairy", "", True),  # a client with no GSTIN
]

ACCOUNTS = [("Sharma Traders", "XXXX4471"), ("Gupta & Sons", "XXXX8823"), ("Mehta Steel", "XXXX1907")]

#: Yesterday's statement lines per account: (narration, party as the bank writes it, credit, debit)
STATEMENT = {
    "XXXX4471": [("NEFT-SHARMA TRADERS PVT LTD-INV11", "SHARMA TRADERS PVT LTD", "1,200.00", ""),
                 ("UPI-GUPTA AND SONS", "GUPTA AND SONS", "5,000.00", ""),
                 ("IMPS-UNKNOWN REMITTER", "MEHTA STEEL PVT LTD", "999.00", ""),
                 ("CHQ RETURN 004512", "RAO AGENCIES", "", "750.00"),
                 ("NEFT-KULKARNI FOODS", "KULKARNI FOODS", "2,360.00", ""),
                 ("NEFT-KULKARNI FOODS", "KULKARNI FOODS", "2,360.00", "")],
    "XXXX8823": [("RTGS-IYER TEXTILES", "IYER TEXTILES", "48,500.00", ""),
                 ("BANK CHARGES", "", "", "118.00")],
    "XXXX1907": [("NEFT-DESAI PHARMA LTD", "DESAI PHARMA LTD", "15,930.00", "")],
}

#: The open invoices in BooksDesk: (invoice, customer, amount)
OPEN_INVOICES = [("INV-11", "Sharma Traders", 1200), ("INV-12", "Gupta and Sons", 5000),
                 ("INV-13", "Rao Agencies", 750), ("INV-14", "Kulkarni Foods", 2360),
                 ("INV-15", "Kulkarni Foods", 2360), ("INV-16", "Iyer Textiles", 48500),
                 ("INV-17", "Desai Pharma", 15930), ("INV-18", "Nair Logistics", 6100)]

SKUS = [  # UrbanKart's SKUs: (sku, title, our price, cost, reorder level)
    ("UK-MUG-01", "Ceramic mug, 350 ml", 349, 160, 40), ("UK-MUG-02", "Travel mug, steel", 699, 330, 25),
    ("UK-LMP-01", "Bedside lamp, oak", 1899, 980, 10), ("UK-LMP-02", "Desk lamp, LED", 1499, 760, 12),
    ("UK-CSH-01", "Cushion cover, 16 in", 299, 120, 60), ("UK-CSH-02", "Cushion cover, 18 in", 349, 140, 50),
    ("UK-RUG-01", "Cotton rug, 3x5 ft", 1299, 650, 8), ("UK-PLN-01", "Planter, terracotta", 449, 190, 30),
    ("UK-CLK-01", "Wall clock, 12 in", 999, 470, 15), ("UK-JAR-01", "Glass jar set of 3", 599, 260, 20),
    ("UK-TWL-01", "Bath towel, 600 GSM", 549, 240, 45), ("UK-TRY-01", "Serving tray, mango wood", 899, 420, 12),
]

#: Competitor prices (pricewatch): sku → price as shown ("" when unavailable)
COMPETITOR = {"UK-MUG-01": "₹ 319", "UK-MUG-02": "₹ 689", "UK-LMP-01": "₹1,749", "UK-LMP-02": "₹ 1,299",
              "UK-CSH-01": "₹ 289", "UK-CSH-02": "", "UK-RUG-01": "₹ 999", "UK-PLN-01": "₹ 459",
              "UK-CLK-01": "₹ 899", "UK-JAR-01": "₹ 579", "UK-TWL-01": "₹ 499", "UK-TRY-01": "₹ 919"}


def _orders() -> List[Dict[str, Any]]:
    rng = random.Random(SEED)
    cities = [("Pune", "411001"), ("Mumbai", "400050"), ("Bengaluru", "560034"), ("Chennai", "600020"),
              ("Kohima", "797001"), ("Delhi", "110017"), ("Jaipur", "302001"), ("Port Blair", "744101")]
    names = ["Asha Rao", "Ravi Kumar", "Meera Iyer", "Arjun Shah", "Fatima Khan", "John D'Souza", "Priya Nair",
             "Karan Mehta", "Sneha Patil", "Vikram Singh"]
    out = []
    for i in range(27):
        sku = SKUS[rng.randrange(len(SKUS))]
        qty = rng.choice([1, 1, 1, 2, 3, 6])
        city, pin = cities[rng.randrange(len(cities))]
        pay = rng.choice(["COD", "Prepaid", "Prepaid"])
        amount = sku[2] * qty
        out.append({"Order ID": f"OD-{70110 + i}", "Date": f"2026-11-0{1 + i % 6}", "Customer": names[i % len(names)],
                    "City": city, "PIN": pin, "SKU": sku[0], "Qty": qty, "Amount": f"₹{amount:,}",
                    "Payment": pay, "Status": "New" if i < 20 else
                    ["Shipped", "RTO", "Delivered", "RTO", "Shipped", "Delivered", "RTO"][i - 20]})
    return out


ORDERS = _orders()
#: PIN codes ShipTrack refuses
REFUSED_PINS = {"797001", "744101"}


def _settlement() -> List[Dict[str, str]]:
    """The marketplace's settlement CSV: most orders paid, two missing, one fee over 2 %."""
    rows = []
    for o in ORDERS[:20]:
        if o["Order ID"] in ("OD-70113", "OD-70119"):
            continue  # not paid out
        amount = int(o["Amount"].replace("₹", "").replace(",", ""))
        fee = round(amount * (0.035 if o["Order ID"] == "OD-70115" else 0.018), 2)
        rows.append({"Order Ref": o["Order ID"].replace("OD-", "OD"), "Settled On": "05/11/2026",
                     "Gross": f"{amount:.2f}", "Marketplace Fee": f"{fee:.2f}", "Net": f"{amount - fee:.2f}"})
    return rows


SETTLEMENT = _settlement()

REVIEWS = [("OD-70111", "Ravi Kumar", 5, "Lovely mug, well packed."),
           ("OD-70114", "Fatima Khan", 2, "Lamp arrived with a dent on the base."),
           ("OD-70117", "Priya Nair", 1, "Wrong colour cushion and no reply from support."),
           ("OD-70118", "Karan Mehta", 4, "Good value."), ("OD-70120", "Sneha Patil", 2, "Clock stopped after a week.")]

CANDIDATES = [  # HireBoard: (id, name, title, years, skills, city, email, phone)
    (100 + i, n, t, y, s, c, e, p) for i, (n, t, y, s, c, e, p) in enumerate([
        ("Ananya Gupta", "UiPath Developer", 4, "UiPath, VB.NET, SQL", "Pune", "ananya.g@example.com", "+91 98220 11001"),
        ("Rohit Verma", "RPA Developer", 6, "Blue Prism, C#, SQL", "Bengaluru", "rohit.v@example.com", "098450 11002"),
        ("Sana Sheikh", "Automation Engineer", 3, "Power Automate, Python", "Pune", "sana.s@example.com", "+91-98220-11003"),
        ("Deepak Rao", "UiPath Lead", 9, "UiPath, Orchestrator, .NET", "Hyderabad", "deepak.r@example.com", "9849011004"),
        ("Ishita Bose", "Business Analyst", 5, "Process mapping, SQL", "Kolkata", "ishita.b@example.com", "+91 98300 11005"),
        ("Manoj Pillai", "RPA Developer", 2, "UiPath, Python", "Kochi", "manoj.p@example.com", "+91 98470 11006"),
        ("Neha Kapoor", "Automation Anywhere Dev", 5, "Automation Anywhere, Java", "Delhi", "neha.k@example.com", "+91 98100 11007"),
        ("Arvind Menon", "UiPath Developer", 7, "UiPath, ReFramework, SQL", "Pune", "arvind.m@example.com", "+91 98220 11008"),
        ("Pooja Shetty", "QA Automation", 4, "Selenium, Java", "Mangaluru", "pooja.s@example.com", "+91 98450 11009"),
        ("Rahul Jain", "RPA Developer", 5, "UiPath, Blue Prism", "Pune", "ROHIT.V@EXAMPLE.COM", "+91 98450 11002"),
        ("Kavya Reddy", "UiPath Developer", 3, "UiPath, Excel automation", "Hyderabad", "kavya.r@example.com", "+91 98490 11011"),
        ("Farhan Ali", "Power Platform Dev", 6, "Power Automate, Power Apps", "Mumbai", "farhan.a@example.com", "+91 98200 11012"),
    ] * 4)]
CANDIDATES = [(100 + i, *c[1:]) for i, c in enumerate(CANDIDATES)]  # 48 profiles over 5 pages

CLAIMS = [  # WarrantyHub: (claim, customer, serial number, repair date, claimed, approved, status, rejection reason, brand)
    ("C-10101", "Customer A. Long", "S-12345", "2026-09-02", "$420.00", "$0.00", "Rejected", "W-16 Missing proof of purchase", "Acme Appliances"),
    ("C-10102", "Customer B. Hart", "s55120", "2026-09-05", "$180.00", "$122.40", "Approved", "", "Acme Appliances"),
    ("C-10103", "Customer C. Diaz", "S-77801", "2026-09-07", "$950.00", "$0.00", "Pending", "", "Blue Ridge Tools"),
    ("C-10104", "Customer D. Moss", "77802", "2026-08-11", "$310.00", "$0.00", "Rejected", "W-29 Filed after the claim window", "Acme Appliances"),
    ("C-10105", "Customer E. Cole", "S-55121", "2026-09-30", "$75.00", "$70.00", "Approved", "", "Blue Ridge Tools"),
    ("C-10106", "Customer F. Ward", "S-55122", "2026-08-20", "$1,240.00", "$0.00", "Rejected", "W-27 Warranty expired", "Blue Ridge Tools"),
    ("C-10107", "Customer G. Lane", "S-55123", "2026-10-01", "$260.00", "$0.00", "Pending", "", "Acme Appliances"),
    ("C-10108", "Customer H. Fox", "S-55124", "2026-09-15", "$530.00", "$0.00", "Rejected", "W-4 Part number does not match", "Acme Appliances"),
    ("C-10109", "Customer I. Gray", "S-55125", "2026-10-12", "$140.00", "$96.00", "Approved", "", "Acme Appliances"),
    ("C-10110", "Customer J. Hale", "S-55126", "2026-09-20", "$880.00", "$0.00", "Pending", "", "Blue Ridge Tools"),
    ("C-10111", "Customer K. Reed", "S-55127", "2026-08-30", "$410.00", "$0.00", "Rejected", "W-97 Covered by an earlier claim", "Acme Appliances"),
    ("C-10112", "Customer L. West", "S-55128", "2026-10-20", "$95.00", "$0.00", "Pending", "", "Acme Appliances"),
    ("C-10113", "Customer M. Shaw", "S-55129", "2026-10-25", "$330.00", "$310.00", "Approved", "", "Blue Ridge Tools"),
    ("C-10114", "Customer N. Cruz", "S-55130", "2026-09-09", "$720.00", "$0.00", "Pending", "", "Acme Appliances"),
    ("C-10115", "Customer O. Park", "S-55131", "2026-07-29", "$615.00", "$0.00", "Rejected", "W-16 Missing information", "Blue Ridge Tools"),
    ("C-10116", "Customer P. King", "S-55132", "2026-10-28", "$210.00", "$0.00", "Pending", "", "Acme Appliances"),
    ("C-10117", "Customer Q. Bell", "S-55133", "2026-09-26", "$505.00", "$455.00", "Approved", "", "Acme Appliances"),
    ("C-10118", "Customer R. Ross", "S-55134", "2026-08-04", "$1,980.00", "$0.00", "Pending", "", "Blue Ridge Tools"),
    ("C-10119", "Customer S. Webb", "S-55135", "2026-09-18", "$365.00", "$0.00", "Rejected", "W-50 Damage not covered", "Acme Appliances"),
    ("C-10120", "Customer T. Knox", "S-55136", "2026-10-05", "$150.00", "$150.00", "Approved", "", "Acme Appliances"),
    ("C-10121", "Customer U. Ford", "S-55137", "2026-10-30", "$275.00", "$0.00", "Pending", "", "Blue Ridge Tools"),
    ("C-10122", "Customer V. Dunn", "S-55138", "2026-08-25", "$440.00", "$0.00", "Rejected", "W-4 Serial number does not match", "Acme Appliances"),
    ("C-10123", "Customer W. Pope", "S-55139", "2026-09-28", "$600.00", "$0.00", "Pending", "", "Acme Appliances"),
]

#: Warranty cover per serial number (as the plan cards show): serial → [(plan, kind, status, service fee)]
COVERAGE = {
    "S-12345": [("Gold Care", "Extended", "Active", "$25"), ("Accident Cover", "Accidental", "Active", "$10")],
    "S-55120": [("Silver Care", "Extended", "Inactive", "$40")],
    "S-77801": [("Parts Plus", "Parts only", "Active", "$15"), ("Bronze Care", "Extended", "Active", "$50")],
    "S-77802": [("Gold Care", "Extended", "Terminated", "$25")],
    "S-55121": [("Gold Care", "Extended", "Active", "$25")],
}

CONTRACTORS = [("Anil Mehta Electricals", "EC-44120", "2027-03-31"), ("Sara Thomas Plumbing", "EC-44121", "2026-12-15"),
             ("Leo Grant Roofing", "EC-44122", "2026-11-30"), ("Nina Patel Builders", "EC-44123", "2028-01-31")]
CAPTCHA_LICENCES = {"EC-44122"}

SUPPLIERS = [("Asha Pharma Distributors", "27AABCA1111A1Z1"), ("Bharat FMCG Supply", "27AABCB2222B1Z2"),
             ("Coastal Medisales", "32AABCC3333C1Z3")]
RECALLS = [("Paracip 500 tablets", "PCP2609A", "Asha Pharma Distributors", "2026-10-28", "Dissolution failure"),
           ("Coughex syrup 100 ml", "CGX2608K", "Coastal Medisales", "2026-10-30", "Microbial contamination")]


# ──────────────────────────────────────────────────────────────────────
# Sites
# ──────────────────────────────────────────────────────────────────────

def _banklite(root: Path) -> None:
    key, nav = "bl_auth", "<b>BankLite NetBanking</b><a href='accounts.html'>Accounts</a><a href='login.html'>Sign out</a>"
    for name, text in _signin("banklite", key, "BankLite NetBanking", "Customer ID", True, "accounts.html").items():
        _write(root, f"banklite/{name}", text)
    rows = "".join(f"<tr><td>{html.escape(n)}</td><td>{a}</td><td><a href='statement.html?acct={a}'>Statement</a></td></tr>"
                   for n, a in ACCOUNTS)
    _write(root, "banklite/accounts.html", page("BankLite: Accounts", f"<h1>Your accounts</h1><table id='accounts'>"
           f"<thead><tr><th>Account name</th><th>Account</th><th></th></tr></thead><tbody>{rows}</tbody></table>",
           nav, _guard(key, "login.html", 30)))
    for acct, lines in STATEMENT.items():
        with open(root / "banklite" / f"statement_{acct}.csv", "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["Txn Date", "Narration", "Party", "Credit", "Debit", "Balance"])
            bal = 100000.0
            for narr, party, cr, dr in lines:
                bal += float((cr or "0").replace(",", "")) - float((dr or "0").replace(",", ""))
                w.writerow(["06/11/2026", narr, party, cr, dr, f"{bal:,.2f}"])
    _write(root, "banklite/statement.html", page("BankLite: Statement", """<h1>Account statement</h1>
<p>Account <b id='acct'></b>. Period: <select id='period'><option>Yesterday</option><option>Last 7 days</option></select></p>
<p><a id='dl' class='btn' download>Download CSV</a></p><div id='rows'></div>""", nav,
           _guard(key, "login.html", 30) + """var a=new URLSearchParams(location.search).get('acct')||'';
document.getElementById('acct').textContent=a;var dl=document.getElementById('dl');
dl.href='statement_'+a+'.csv';dl.setAttribute('download','statement_'+a+'.csv');"""))


def _ledgertax(root: Path) -> None:
    key = "lt_auth"
    nav = ("<b>LedgerTax GST Portal</b><a href='returns.html'>Returns</a><a href='challans.html'>Challans</a>"
           "<a href='itr.html'>Income tax</a>")
    for name, text in _signin("ledgertax", key, "LedgerTax GST Portal", "Username", True, "returns.html").items():
        _write(root, f"ledgertax/{name}", text)
    rows = [[g, n, "GSTR-1", "Oct-2026", "Filed" if f else "Not filed", "10/11/2026" if f else ""]
            for n, g, f in CLIENTS if g]
    for p, chunk in enumerate([rows[:8], rows[8:]], 1):
        pager = ("<p class='pager'>" + ("<a href='returns.html'>Previous</a>" if p == 2 else "") +
                 f"Page {p} of 2" + ("<a href='returns_2.html'>Next</a>" if p == 1 else "") + "</p>")
        _write(root, f"ledgertax/returns{'' if p == 1 else '_2'}.html", page(
            "LedgerTax: Returns", "<h1>Return filing status</h1><p>Search GSTIN <input id='q'> "
            "<button class='btn' onclick=\"var q=document.getElementById('q').value.trim().toUpperCase();"
            "document.querySelectorAll('#returns tbody tr').forEach(function(r){r.style.display=!q||r.cells[0].textContent==q?'':'none'})\">"
            "Search</button></p>" + _table(["GSTIN", "Legal name", "Return", "Period", "Status", "Filed on"], chunk,
                                            "returns") + pager, nav, _guard(key, "login.html", 10)))
    ch = [[n, f"CHL{8800 + i}", "Oct-2026", f"challan_{8800 + i}.pdf"] for i, (n, g, f) in enumerate(CLIENTS[:6])]
    body = "".join(f"<tr><td>{html.escape(n)}</td><td>{c}</td><td>{m}</td><td><a href='#' class='dl' data-file='{f}'>"
                   f"Download</a></td></tr>" for n, c, m, f in ch)
    for _n, c, _m, f in ch:
        (root / "ledgertax" / f).write_bytes(b"%PDF-1.4\n% TDS challan " + c.encode() + b"\n%%EOF\n")
    _write(root, "ledgertax/challans.html", page("LedgerTax: Challans", "<h1>TDS challans</h1><table id='challans'>"
           "<thead><tr><th>Client</th><th>Challan</th><th>Month</th><th></th></tr></thead><tbody>" + body +
           "</tbody></table><p id='wait'></p>", nav, _guard(key, "login.html", 10) + """
document.querySelectorAll('a.dl').forEach(function(a){a.addEventListener('click',function(e){e.preventDefault();
var w=document.getElementById('wait');w.textContent='Preparing your file…';setTimeout(function(){w.textContent='';
var l=document.createElement('a');l.href=a.dataset.file;l.download=a.dataset.file;document.body.appendChild(l);l.click();},3000);});});"""))
    _write(root, "ledgertax/itr.html", page("LedgerTax: Income tax login", """<h1>Income tax e-filing</h1>
<form onsubmit='return false'><label for='pan'>PAN</label><input id='pan'><label for='cap'>Type the characters in the picture</label>
<canvas id='captcha' width='160' height='48' aria-label='captcha picture'></canvas><input id='cap' autocomplete='off'>
<p><button class='btn'>Continue</button></p></form>""", nav, """var c=document.getElementById('captcha').getContext('2d');
c.font='28px serif';c.fillStyle='#345';c.setTransform(1,.2,-.3,1,8,4);c.fillText('x7Kq2',10,30);"""))


def _shopseller(root: Path) -> None:
    key = "ss_auth"
    nav = ("<b>Seller Central · UrbanKart</b><a href='orders.html'>Orders</a><a href='returns.html'>Returns</a>"
           "<a href='pricing.html'>Pricing</a><a href='reviews.html'>Reviews</a><a href='listing_new.html'>Add listing</a>")
    popup = """setTimeout(function(){if(Math.random()<0.6&&!sessionStorage.getItem('ss_offer_'+location.pathname)){
var m=document.createElement('div');m.className='modal';m.id='festive';m.innerHTML="<div role='dialog' aria-label='Festive offer'>"+
"<h2>Festive Sale is LIVE 🎉</h2><p>Boost your listings with 20% off ads till Sunday.</p><button class='btn' id='boost'>Boost now</button> "+
"<button id='festive-close' aria-label='Close'>×</button></div>";document.body.appendChild(m);
document.getElementById('festive-close').onclick=function(){m.remove();sessionStorage.setItem('ss_offer_'+location.pathname,'1')};}},1200);"""
    for name, text in _signin("shopseller", key, "Seller Central", "Seller email", False, "orders.html").items():
        _write(root, f"shopseller/{name}", text)
    guard = _guard(key, "login.html", 30)
    cols = ["Order ID", "Date", "Customer", "City", "PIN", "SKU", "Qty", "Amount", "Payment", "Status"]
    pages = [ORDERS[i:i + 10] for i in range(0, len(ORDERS), 10)]
    for p, chunk in enumerate(pages, 1):
        name = "orders.html" if p == 1 else f"orders_{p}.html"
        prev = ("orders.html" if p == 2 else f"orders_{p - 1}.html") if p > 1 else ""
        nxt = f"orders_{p + 1}.html" if p < len(pages) else ""
        pager = (f"<p class='pager'>{'<a href=' + repr(prev) + '>Previous</a>' if prev else ''} Page {p} of {len(pages)} "
                 f"{'<a href=' + repr(nxt) + ' class=next>Next</a>' if nxt else ''}</p>")
        # The very first visit shows the error page: a bot must try again.
        down = ("var n=+(localStorage.getItem('ss_sessions')||0);if(!sessionStorage.getItem('ss_seen')){"
                "sessionStorage.setItem('ss_seen','1');localStorage.setItem('ss_sessions',n+1);}"
                "if(n==0&&!sessionStorage.getItem('ss_up')){sessionStorage.setItem('ss_up','1');document.title='Error 500';"
                "document.querySelector('main').innerHTML=\"<h1>500 · Something went wrong</h1><p>Our servers are busy. "
                "Please try again in a few minutes.</p>\";}") if p == 1 else ""
        _write(root, f"shopseller/{name}", page("Seller Central: Orders", "<h1>Orders</h1>" + _table(
            cols, [[o[c] for c in cols] for o in chunk], "orders") + pager, nav, guard + down + popup))
    rto = [o for o in ORDERS if o["Status"] == "RTO"]
    body = "".join(f"<tr><td>{o['Order ID']}</td><td>{o['SKU']}</td><td>{o['Qty']}</td><td class='st'>Return in transit</td>"
                   f"<td><button class='btn mark'>Mark returned</button></td></tr>" for o in rto)
    _write(root, "shopseller/returns.html", page("Seller Central: Returns", "<h1>Returns (RTO)</h1><table id='returns'>"
           "<thead><tr><th>Order ID</th><th>SKU</th><th>Qty</th><th>Status</th><th></th></tr></thead><tbody>" + body +
           "</tbody></table>", nav, guard + """document.querySelectorAll('.mark').forEach(function(b){b.onclick=function(){
if(confirm('Mark this order as returned to the warehouse? This cannot be undone.')){var r=b.closest('tr');
r.querySelector('.st').textContent='Returned';b.remove();}};});""" + popup))
    prow = "".join(f"<tr><td>{s}</td><td>{html.escape(t)}</td><td class='price'>₹{p:,}</td><td>"
                   f"<input class='newprice' aria-label='New price for {s}' size='6'><button class='btn save'>Save</button>"
                   f"<span class='ok'></span></td></tr>" for s, t, p, _c, _r in SKUS)
    _write(root, "shopseller/pricing.html", page("Seller Central: Pricing", "<h1>Your prices</h1><table id='pricing'>"
           "<thead><tr><th>SKU</th><th>Title</th><th>Price</th><th>Change price</th></tr></thead><tbody>" + prow +
           "</tbody></table>", nav, guard + """document.querySelectorAll('.save').forEach(function(b){b.onclick=function(){
var r=b.closest('tr'),v=r.querySelector('.newprice').value.replace(/[^0-9.]/g,'');if(!v){return;}
r.querySelector('.price').textContent='₹'+(+v).toLocaleString('en-IN');r.querySelector('.ok').textContent=' Saved';};});"""))
    rv = "".join(f"<div class='review' data-order='{o}'><h3>{'★' * s}{'☆' * (5 - s)} <span class='stars'>{s}</span>/5</h3>"
                 f"<p class='who'>{html.escape(n)} · {o}</p><p class='text'>{html.escape(t)}</p>"
                 f"<details><summary>Reply</summary><textarea aria-label='Reply to {html.escape(n)}'></textarea>"
                 f"<button class='btn send'>Post reply</button></details></div>" for o, n, s, t in REVIEWS)
    _write(root, "shopseller/reviews.html", page("Seller Central: Reviews", "<h1>Customer reviews</h1>" + rv, nav, guard))
    _write(root, "shopseller/listing_new.html", page("Seller Central: Add a listing", """<h1>Add a listing</h1>
<form id='listing' onsubmit='return add()'><label for='sku'>SKU</label><input id='sku' required>
<label for='title'>Title</label><input id='title' required><label for='price'>Selling price (₹)</label><input id='price' required>
<label for='mrp'>MRP (₹)</label><input id='mrp'><label for='cat'>Category</label><select id='cat'><option value=''>Choose…</option>
<option>Kitchen &amp; Dining</option><option>Home Decor</option><option>Lighting</option><option>Bath</option></select>
<label for='img'>Main image</label><input id='img' type='file' accept='image/*'>
<p><button class='btn' type='submit'>Create listing</button></p></form><p id='done' class='ok'></p>""", nav,
           guard + """function add(){var n=document.getElementById('img').files.length;if(!n){alert('Add the main image.');return false;}
document.getElementById('done').textContent='Listing created: L-'+(4000+Math.floor(Math.random()*999))+' for '+document.getElementById('sku').value;return false;}"""))
    # The competitor's page (pricewatch): one product card per SKU, some unavailable.
    cards = "".join(f"<div class='product' data-sku='{s}'><h3>{html.escape(t)}</h3><p class='sku'>Model: {s}</p>"
                    + (f"<p class='price'>{COMPETITOR[s]}</p>" if COMPETITOR[s] else "<p class='oos'>Currently unavailable</p>")
                    + "</div>" for s, t, *_ in SKUS)
    _write(root, "pricewatch/index.html", page("HomeMart: Home & kitchen", "<h1>HomeMart · Home &amp; kitchen</h1>" + cards))
    with open(root / "shopseller" / "settlement_2026-11-05.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(SETTLEMENT[0]))
        w.writeheader()
        w.writerows(SETTLEMENT)


def _shiptrack(root: Path) -> None:
    nav = "<b>ShipTrack Couriers</b><a href='create.html'>Book a shipment</a><a href='track.html'>Track</a>"
    refused = json.dumps(sorted(REFUSED_PINS))
    _write(root, "shiptrack/create.html", page("ShipTrack: Book a shipment", """<h1>Book a shipment</h1>
<form id='book' onsubmit='return book()'><label for='ref'>Your order reference</label><input id='ref' required>
<label for='name'>Receiver name</label><input id='name' required><label for='pin'>Delivery PIN code</label><input id='pin' required>
<label for='wt'>Weight (kg)</label><input id='wt' value='0.5'><label for='mode'>Payment</label><select id='mode'><option>Prepaid</option><option>COD</option></select>
<p><button class='btn' type='submit'>Book</button></p></form><p id='out'></p>""", nav,
           f"""var REFUSED={refused};function book(){{var p=document.getElementById('pin').value.trim(),o=document.getElementById('out');
if(REFUSED.indexOf(p)>=0){{o.className='err';o.textContent='We do not deliver to PIN code '+p+' yet.';return false;}}
var r=document.getElementById('ref').value.trim();o.className='ok';o.innerHTML='Booked. AWB number: <b id="awb">ST'+r.replace(/\\D/g,'')+'IN</b>';return false;}}"""))
    status = {}
    for o in ORDERS:
        n = int(o["Order ID"][3:])
        status[f"ST{n}IN"] = ["In transit", "Delivered", "Out for delivery", "RTO initiated", "In transit"][n % 5]
    stuck = {f"ST{int(o['Order ID'][3:])}IN": "2026-10-31" for o in ORDERS[3:6]}
    _write(root, "shiptrack/track.html", page("ShipTrack: Track", """<h1>Track a shipment</h1>
<form onsubmit='return track()'><label for='awb'>AWB number</label><input id='awb'><button class='btn'>Track</button></form>
<div id='res'></div>""", nav, f"""var S={json.dumps(status)},STUCK={json.dumps(stuck)};
function track(){{var n=+(localStorage.getItem('st_n')||0)+1,t0=+(localStorage.getItem('st_t')||0),r=document.getElementById('res');
if(Date.now()-t0>60000){{n=1;localStorage.setItem('st_t',Date.now());}}localStorage.setItem('st_n',n);
if(n>20){{r.innerHTML="<p class='err'>Too many requests. Please try again in a minute.</p>";return false;}}
var a=document.getElementById('awb').value.trim().toUpperCase();r.innerHTML='<p>Looking up…</p>';
setTimeout(function(){{if(!S[a]){{r.innerHTML="<p class='err'>No shipment found for "+a+"</p>";return;}}
r.innerHTML="<table id='tracking'><tr><th>AWB</th><td>"+a+"</td></tr><tr><th>Status</th><td id='status'>"+S[a]+"</td></tr>"+
"<tr><th>Last update</th><td id='updated'>"+(STUCK[a]||'2026-11-06')+"</td></tr></table>";}},800);return false;}}"""))


def _warrantyhub(root: Path) -> None:
    key = "wh_auth"
    nav = ("<b>WarrantyHub Partner Portal</b><a href='claims.html'>Claims</a><a href='coverage.html'>Warranty check</a>"
           "<a href='service_request.html'>Service request</a>")
    for name, text in _signin("warrantyhub", key, "WarrantyHub Partner Portal", "User ID", False, "claims.html").items():
        _write(root, f"warrantyhub/{name}", text)
    guard = _guard(key, "login.html", 10)  # signs out after 10 minutes without a click
    data = json.dumps([[c[0], c[1], c[3], c[4], c[6]] for c in CLAIMS])
    _write(root, "warrantyhub/claims.html", page("WarrantyHub: Claim search", """<h1>Claim search</h1>
<form onsubmit='return false'><label for='st'>Status</label><select id='st'><option>Any</option><option>Approved</option><option>Rejected</option><option>Pending</option></select>
<button id='search' class='btn'>Search</button></form>
<table id='results'><thead><tr><th>Claim</th><th>Customer</th><th>Repair date</th><th>Claimed</th><th>Status</th><th></th></tr></thead><tbody></tbody></table>
<div class='pager'><button class='pager-prev'>Previous</button><span class='pageinfo'></span><button class='pager-next'>Next</button></div>""",
           nav, guard + f"""var all={data},data=all,page=0;function show(){{var tb=document.querySelector('#results tbody');tb.innerHTML='';
data.slice(page*10,page*10+10).forEach(function(r){{var tr=document.createElement('tr');tr.innerHTML=r.map(function(c){{return '<td>'+c+'</td>'}}).join('')+
'<td><a href="claim.html?id='+r[0]+'">View</a></td>';tb.appendChild(tr);}});var n=Math.max(1,Math.ceil(data.length/10));
document.querySelector('.pageinfo').textContent='Page '+(page+1)+' of '+n;document.querySelector('.pager-next').disabled=page>=n-1;
document.querySelector('.pager-prev').disabled=page==0;}}
document.querySelector('.pager-next').onclick=function(){{page++;show()}};document.querySelector('.pager-prev').onclick=function(){{page--;show()}};
document.getElementById('search').onclick=function(){{var s=document.getElementById('st').value;data=s=='Any'?all:all.filter(function(r){{return r[4]==s}});page=0;show();}};show();"""))
    detail = {c[0]: {"Claim": c[0], "Customer": c[1], "Serial number": c[2], "Repair date": c[3], "Claimed": c[4],
                     "Approved": c[5], "Status": c[6], "Rejection reason": c[7] or "—", "Brand": c[8]} for c in CLAIMS}
    _write(root, "warrantyhub/claim.html", page("WarrantyHub: Claim", "<h1>Claim details</h1><dl id='claim'></dl>", nav,
           guard + f"""var D={json.dumps(detail)},c=D[new URLSearchParams(location.search).get('id')];var dl=document.getElementById('claim');
if(!c){{dl.textContent='Claim not found';}}else{{Object.keys(c).forEach(function(k){{dl.innerHTML+='<dt>'+k+'</dt><dd class="'+k.toLowerCase().replace(/ /g,'-')+'">'+c[k]+'</dd>';}});}}"""))
    cover = json.dumps(COVERAGE)
    _write(root, "warrantyhub/coverage.html", page("WarrantyHub: Warranty check", "<h1>Warranty check</h1>"
           "<serial-search id='ms'></serial-search><div id='plans'></div>", nav, guard + f"""var E={cover};
var r=document.getElementById('ms').attachShadow({{mode:'closed'}});
r.innerHTML='<label for="mid">Serial number</label><input id="mid" placeholder="e.g. S-12345"><label for="pdate">Purchase date</label>'+
'<input id="pdate" placeholder="MM/DD/YYYY"><button id="go">Check warranty</button>';
r.getElementById('go').addEventListener('click',function(){{var m=r.getElementById('mid').value.trim().toUpperCase().replace(/^S?-?/,'S-');
var out=document.getElementById('plans');out.innerHTML='<p>Checking…</p>';setTimeout(function(){{var p=E[m];
if(!p){{out.innerHTML='<p class="err">No coverage found for '+m+'</p>';return;}}
out.innerHTML='<h2>Warranty for serial number '+m+'</h2>'+p.map(function(x){{return '<div class="plan-card"><h3 class="plan-name">'+x[0]+'</h3>'+
'<div class="row"><span class="lbl">Coverage type</span> <span class="val">'+x[1]+'</span></div><div class="row"><span class="lbl">Service fee</span> '+
'<span class="val fee">'+x[3]+'</span></div><span class="badge status">'+x[2]+'</span></div>'}}).join('');}},1500);}});"""))
    _write(root, "warrantyhub/service_request.html", page("WarrantyHub: Service request", """<h1>Request a service visit</h1>
<form id='sr' onsubmit='return send()'><label for='mid'>Serial number</label><input id='mid' required>
<label for='part'>Part code</label><input id='part' required placeholder='PT-4471'><label for='vdate'>Planned date</label><input id='vdate' type='date' required>
<label for='doc'>Invoice / photos (PDF)</label><input id='doc' type='file' accept='application/pdf'>
<p><button class='btn' type='submit'>Submit request</button></p></form><p id='ref' class='ok'></p>""", nav, guard + """function send(){
if(!document.getElementById('doc').files.length){alert('Attach the invoice (PDF).');return false;}
if(!confirm('Submit this service request?'))return false;
document.getElementById('ref').innerHTML='Submitted. Reference: <b id="sr-ref">SR-'+document.getElementById('mid').value.replace(/\\D/g,'')+'-'+document.getElementById('part').value+'</b>';return false;}"""))
    rows = "".join(f"<tr><td>{html.escape(n)}</td><td>{l}</td></tr>" for n, l, _e in CONTRACTORS)
    expiry = json.dumps({lic: e for _n, lic, e in CONTRACTORS})
    _write(root, "licence/index.html", page("State Contractor Board: Licence lookup", """<h1>State Contractor Board · Licence verification</h1>
<form onsubmit='return look()'><label for='lic'>Licence number</label><input id='lic'><button class='btn'>Look up</button></form><div id='res'></div>""",
           script=f"""var X={expiry},CAP={json.dumps(sorted(CAPTCHA_LICENCES))};function look(){{var l=document.getElementById('lic').value.trim().toUpperCase(),r=document.getElementById('res');
if(CAP.indexOf(l)>=0){{r.innerHTML="<h2>Verify you are human</h2><canvas width='150' height='44' aria-label='captcha'></canvas><input aria-label='Characters'><button>Verify</button>";return false;}}
r.innerHTML=X[l]?"<table id='licence'><tr><th>Licence</th><td>"+l+"</td></tr><tr><th>Status</th><td>Active</td></tr><tr><th>Expires</th><td id='expires'>"+X[l]+"</td></tr></table>":"<p class='err'>No licence "+l+"</p>";return false;}}"""))
    del rows


def _hireboard(root: Path) -> None:
    nav = "<b>HireBoard · Recruiter</b><a href='search.html'>Search candidates</a>"
    data = json.dumps([{"id": c[0], "name": c[1], "title": c[2], "years": c[3], "skills": c[4], "city": c[5]}
                       for c in CANDIDATES])
    _write(root, "hireboard/search.html", page("HireBoard: Search", """<h1>Search candidates</h1>
<form onsubmit='return find()'><label for='q'>Keywords</label><input id='q' placeholder='UiPath'><label for='city'>City</label><input id='city'>
<button class='btn'>Search</button></form><p id='count'></p><ol id='list'></ol><p class='pager'><a href='#' id='prev'>Previous</a> <span id='pg'></span> <a href='#' id='next'>Next</a></p>""",
           nav, f"""var C={data},hits=[],page=0;function find(){{var q=document.getElementById('q').value.toLowerCase(),c=document.getElementById('city').value.toLowerCase();
hits=C.filter(function(x){{return (!q||(x.title+' '+x.skills).toLowerCase().indexOf(q)>=0)&&(!c||x.city.toLowerCase()==c)}});page=0;show();return false;}}
function show(){{var l=document.getElementById('list');l.innerHTML='';hits.slice(page*10,page*10+10).forEach(function(x){{l.innerHTML+='<li class="cand"><a href="profile.html?id='+x.id+'">'+x.name+'</a> · <span class="title">'+x.title+'</span> · '+x.city+'</li>'}});
document.getElementById('count').textContent=hits.length+' candidates';document.getElementById('pg').textContent='Page '+(page+1)+' of '+Math.max(1,Math.ceil(hits.length/10));}}
document.getElementById('next').onclick=function(e){{e.preventDefault();if((page+1)*10<hits.length){{page++;show();}}}};
document.getElementById('prev').onclick=function(e){{e.preventDefault();if(page>0){{page--;show();}}}};hits=C;show();"""))
    prof = json.dumps({str(c[0]): {"Name": c[1], "Title": c[2], "Experience": f"{c[3]} years", "Skills": c[4],
                                   "City": c[5], "Email": c[6], "Phone": c[7]} for c in CANDIDATES})
    _write(root, "hireboard/profile.html", page("HireBoard: Profile", "<h1 id='name'></h1><dl id='profile'></dl>", nav,
           f"""var P={prof},id=new URLSearchParams(location.search).get('id'),p=P[id],n=+(localStorage.getItem('hb_views')||0)+1;
localStorage.setItem('hb_views',n);if(n>100){{document.querySelector('main').innerHTML='<h1>Daily view limit reached</h1><p>You can view 100 profiles a day.</p>';}}
else if(+id%7==0){{document.querySelector('main').innerHTML="<h1>Verify you are human</h1><canvas width='150' height='44' aria-label='captcha'></canvas><input aria-label='Characters'>";}}
else if(p){{document.getElementById('name').textContent=p.Name;Object.keys(p).forEach(function(k){{document.getElementById('profile').innerHTML+='<dt>'+k+'</dt><dd class="'+k.toLowerCase()+'">'+p[k]+'</dd>'}});}}"""))


def _peoplehub(root: Path) -> None:
    nav = "<b>PeopleHub ATS</b><a href='index.html'>Candidates</a><a href='feedback.html'>Feedback</a>"
    _write(root, "peoplehub/form.html", page("Add candidate", """<form id='add' onsubmit='return add()'>
<label for='n'>Full name</label><input id='n' required><label for='e'>Email</label><input id='e' type='email' required>
<label for='r'>Requisition</label><select id='r'><option value=''>Choose…</option><option>REQ-201 UiPath Developer</option><option>REQ-202 RPA Lead</option></select>
<label for='cv'>Resume (PDF)</label><input id='cv' type='file'><p><button class='btn'>Add candidate</button></p></form><p id='ok' class='ok'></p>""",
           script="""function add(){if(!window.confirm('Add this candidate to the requisition?'))return false;
document.getElementById('ok').textContent='Candidate added: CAND-'+(500+Math.floor(Math.random()*400));return false;}"""))
    _write(root, "peoplehub/index.html", page("PeopleHub: Candidates", "<h1>Add a candidate</h1>"
           "<iframe src='form.html' title='Add candidate form' width='620' height='420'></iframe>"
           "<h2>Pipeline</h2>" + _table(["Candidate", "Requisition", "Stage"], [
               ["Ananya Gupta", "REQ-201", "Interview"], ["Arvind Menon", "REQ-201", "Screening"],
               ["Deepak Rao", "REQ-202", "Offer"]], "pipeline"), nav))
    _write(root, "peoplehub/feedback.html", page("PeopleHub: Feedback", "<h1>Interview feedback</h1>" + _table(
        ["Candidate", "Interviewer", "Slot", "Rating", "Recommendation"],
        [["Ananya Gupta", "Kiran S", "2026-11-05 11:00", "4", "Hire"], ["Arvind Menon", "Kiran S", "2026-11-06 15:00", "", ""]],
        "feedback"), nav))


def _vendornet(root: Path) -> None:
    nav = "<b>VendorNet Supplier Portal</b><a href='index.html'>Purchase orders</a><a href='invoices.html'>Invoices</a>"
    pos = json.dumps([["PO-5501", "Asha Pharma Distributors", "2026-11-04", "2026-11-12", "Open"],
                      ["PO-5502", "Bharat FMCG Supply", "2026-11-04", "2026-11-20", "Open"],
                      ["PO-5503", "Coastal Medisales", "2026-11-05", "2026-11-09", "Acknowledged"]])
    _write(root, "vendornet/index.html", page("VendorNet: Purchase orders", "<h1>Purchase orders</h1><po-list></po-list>"
           "<h2>Spend this quarter</h2><canvas id='chart' width='420' height='160' aria-label='spend by supplier chart'></canvas>",
           nav, f"""customElements.define('po-list',class extends HTMLElement{{connectedCallback(){{var r=this.attachShadow({{mode:'open'}}),P={pos};
r.innerHTML='<table><thead><tr><th>PO</th><th>Supplier</th><th>Raised</th><th>Promised</th><th>Status</th><th></th></tr></thead><tbody>'+
P.map(function(p){{return '<tr><td>'+p.join('</td><td>')+'</td><td>'+(p[4]=='Open'?'<button class="ack">Acknowledge</button>':'')+'</td></tr>'}}).join('')+'</tbody></table>';
r.querySelectorAll('.ack').forEach(function(b){{b.onclick=function(){{b.closest('tr').children[4].textContent='Acknowledged';b.remove();}}}});}}}});
var c=document.getElementById('chart').getContext('2d'),v=[[' Asha',120],[' Bharat',80],[' Coastal',45]];v.forEach(function(x,i){{c.fillStyle='#2563eb';
c.fillRect(20+i*130,150-x[1],80,x[1]);c.fillStyle='#000';c.fillText(x[0]+' ₹'+x[1]+'k',20+i*130,150-x[1]-6);}});"""))
    _write(root, "vendornet/invoices.html", page("VendorNet: Invoices", "<h1>Invoices</h1>" + _table(
        ["Invoice", "Supplier", "PO", "Date", "Amount"],
        [["SI-9001", "Asha Pharma Distributors", "PO-5501", "06/11/2026", "₹1,18,000"],
         ["SI-9002", "Bharat FMCG Supply", "PO-5502", "06/11/2026", "₹64,900"]], "invoices"), nav))
    recall = _table(["Product", "Batch", "Supplier", "Recalled on", "Reason"], [list(r) for r in RECALLS], "recalls")
    _write(root, "recalls/index.html", page("Drug Regulator: Recalls", "<h1>Drug recalls</h1>" + recall))


def _misc(root: Path) -> None:
    banks = {"HDFC0001234": ("HDFC Bank", "Baner, Pune"), "SBIN0005678": ("State Bank of India", "MG Road, Bengaluru"),
             "ICIC0000999": ("ICICI Bank", "Andheri East, Mumbai")}
    _write(root, "ifsc/index.html", page("IFSC lookup", """<h1>IFSC code lookup</h1><form onsubmit='return f()'>
<label for='i'>IFSC</label><input id='i'><button class='btn'>Find</button></form><div id='r'></div>""",
           script=f"""var B={json.dumps(banks)};function f(){{var i=document.getElementById('i').value.trim().toUpperCase(),b=B[i],r=document.getElementById('r');
r.innerHTML=b?"<p>Bank: <b id='bank'>"+b[0]+"</b></p><p>Branch: <b id='branch'>"+b[1]+"</b></p>":"<p class='err'>Invalid IFSC</p>";return false;}}"""))
    _write(root, "helpdesk/index.html", page("IT Helpdesk: New request", """<h1>New IT request</h1><form onsubmit='return s()'>
<label for='who'>For employee</label><input id='who'><label for='kind'>Request</label><select id='kind'><option>Laptop</option><option>Email account</option><option>Access</option></select>
<label for='note'>Details</label><textarea id='note'></textarea><p><button class='btn'>Submit</button></p></form><p id='t' class='ok'></p>""",
           script="function s(){document.getElementById('t').textContent='Ticket IT-'+(7000+Math.floor(Math.random()*900))+' raised';return false;}"))
    _write(root, "bgv/index.html", page("VerifyFast BGV", """<h1>Background verification</h1><form onsubmit='return s()'>
<label for='n'>Candidate name</label><input id='n'><label for='pan'>PAN</label><input id='pan'><p><button class='btn'>Submit</button></p></form>
<h2>Status</h2><table id='status'><tr><th>Candidate</th><th>Status</th></tr><tr><td>Ananya Gupta</td><td>In progress</td></tr><tr><td>Deepak Rao</td><td>Clear</td></tr></table>""",
           script="function s(){alert('Submitted');return false;}"))


SITES = ("banklite", "ledgertax", "shopseller", "pricewatch", "shiptrack", "warrantyhub", "licence", "hireboard",
         "peoplehub", "vendornet", "recalls", "ifsc", "helpdesk", "bgv")


def build(root: Path) -> None:
    """Every site of the practice world under ``root`` (served as ``<url><site>/...``)."""
    for make in (_banklite, _ledgertax, _shopseller, _shiptrack, _warrantyhub, _hireboard, _peoplehub, _vendornet,
                 _misc):
        make(root)
