// The Celers RPA Benchmark's practice desktop apps.
//
//   PracticeApps.exe --app books|stock|payroll --data FILE.json --log FILE.jsonl [--title TEXT]
//
// BooksDesk (Tally-like accounting: keyboard-only voucher entry, Ctrl+A accepts), StockKeeper (inventory: an
// editable grid, confirmations, a slow refresh, a record locked once) and PayrollPro (payroll: tabs, Import CSV through
// an Open dialog, Export through a Save dialog). Any of them can be shown to a
// bot as pixels only through the Remote Lab (remotelab/). Every change is written to the journal as one JSON line, so a run is
// judged on what it did. Compiled by build.py with the C# 5 compiler that ships with Windows.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Celers.Practice
{
    static class Journal
    {
        public static string Path = "";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static void Write(string app, string evt, Dictionary<string, object> data)
        {
            if (string.IsNullOrEmpty(Path)) return;
            var row = new Dictionary<string, object>();
            row["t"] = DateTime.Now.ToString("o");
            row["app"] = app;
            row["event"] = evt;
            foreach (var kv in data) row[kv.Key] = kv.Value;
            try { File.AppendAllText(Path, Json.Serialize(row) + "\n", Encoding.UTF8); } catch (IOException) { }
        }

        public static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    static class Ui
    {
        public static Label L(string text, int x, int y) { return new Label { Text = text, Left = x, Top = y + 3, AutoSize = true }; }

        public static TextBox T(string name, int x, int y, int w)
        {
            return new TextBox { Name = name, Left = x, Top = y, Width = w, AccessibleName = name };
        }

        public static Button B(string name, string text, int x, int y, int w)
        {
            return new Button { Name = name, Text = text, Left = x, Top = y, Width = w, Height = 28 };
        }

        public static DataGridView Grid(string name, int x, int y, int w, int h, params string[] cols)
        {
            var g = new DataGridView
            {
                Name = name, Left = x, Top = y, Width = w, Height = h, AllowUserToAddRows = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AccessibleName = name
            };
            foreach (var c in cols) g.Columns.Add(c.Replace(" ", ""), c);
            return g;
        }

        public static string Csv(DataGridView g)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", g.Columns.Cast<DataGridViewColumn>().Select(c => Q(c.HeaderText))));
            foreach (DataGridViewRow r in g.Rows)
                sb.AppendLine(string.Join(",", r.Cells.Cast<DataGridViewCell>().Select(c => Q(Convert.ToString(c.Value)))));
            return sb.ToString();
        }

        static string Q(string s) { s = s ?? ""; return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }

        public static bool Save(IWin32Window owner, string suggested, string text, string app)
        {
            using (var dlg = new SaveFileDialog { Title = "Export", FileName = suggested, Filter = "CSV files (*.csv)|*.csv|All files|*.*" })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                File.WriteAllText(dlg.FileName, text, new UTF8Encoding(true));
                Journal.Write(app, "exported", Journal.D("file", dlg.FileName));
                return true;
            }
        }

        public static decimal? Money(string s)
        {
            s = Regex.Replace(s ?? "", @"(?i)rs\.?|inr|₹|\$|,|/-|\s", "");
            decimal v;
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v) ? v : (decimal?)null;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // BooksDesk: Tally-like accounting
    // ──────────────────────────────────────────────────────────────────
    class BooksDesk : Form
    {
        readonly ComboBox type = new ComboBox { Name = "VoucherType", DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Voucher type" };
        readonly TextBox date, party, refNo, amount, gst, narration;
        readonly Label status = new Label { Name = "Status", AutoSize = true, ForeColor = Color.DarkGreen };
        readonly DataGridView invoices, daybook;
        readonly List<Control> order = new List<Control>();

        public BooksDesk(Dictionary<string, object> data, string title)
        {
            Text = string.IsNullOrEmpty(title) ? "BooksDesk - Sharma & Co." : title;
            Width = 900; Height = 620; KeyPreview = true;
            var tabs = new TabControl { Name = "Tabs", Dock = DockStyle.Fill };
            var entry = new TabPage("Voucher Entry") { Name = "pgVoucher" };
            type.Items.AddRange(new object[] { "Receipt", "Payment", "Purchase", "Sales" });
            type.SelectedIndex = 2; type.Left = 160; type.Top = 20; type.Width = 200;
            entry.Controls.Add(Ui.L("Voucher type", 20, 20)); entry.Controls.Add(type);
            date = Add(entry, "Date (dd-mm-yyyy)", "VoucherDate", 60);
            party = Add(entry, "Party A/c name", "Party", 100);
            refNo = Add(entry, "Ref / Invoice No", "RefNo", 140);
            amount = Add(entry, "Amount", "Amount", 180);
            gst = Add(entry, "GST %", "GstRate", 220);
            narration = Add(entry, "Narration", "Narration", 260);
            entry.Controls.Add(new Label { Text = "Enter: next field    Ctrl+A: Accept    Esc: Clear", Left = 20, Top = 310, AutoSize = true, ForeColor = Color.DimGray });
            status.Left = 20; status.Top = 340; entry.Controls.Add(status);
            order.AddRange(new Control[] { type, date, party, refNo, amount, gst, narration });

            var open = new TabPage("Open Invoices") { Name = "pgInvoices" };
            invoices = Ui.Grid("OpenInvoices", 10, 10, 840, 440, "Invoice", "Customer", "Amount", "Status", "Receipt Ref");
            foreach (var o in (ArrayList)data["open_invoices"])
            {
                var r = (ArrayList)o;
                invoices.Rows.Add(r[0], r[1], r[2], "Open", "");
            }
            var mark = Ui.B("MarkReceived", "&Mark received", 10, 460, 160);
            mark.Click += delegate { MarkReceived(); };
            open.Controls.Add(invoices); open.Controls.Add(mark);

            var book = new TabPage("Day Book") { Name = "pgDayBook" };
            daybook = Ui.Grid("DayBook", 10, 10, 840, 440, "Type", "Date", "Party", "Ref", "Amount", "GST %", "Narration");
            foreach (var o in (ArrayList)data["vouchers"])
            {
                var r = (ArrayList)o;
                daybook.Rows.Add(r.ToArray());
            }
            var export = Ui.B("Export", "&Export...", 10, 460, 120);
            export.Click += delegate { Ui.Save(this, "daybook.csv", Ui.Csv(daybook), "books"); };
            book.Controls.Add(daybook); book.Controls.Add(export);

            tabs.TabPages.Add(entry); tabs.TabPages.Add(open); tabs.TabPages.Add(book);
            Controls.Add(tabs);
            KeyDown += OnKey;
        }

        TextBox Add(TabPage p, string label, string name, int y)
        {
            p.Controls.Add(Ui.L(label, 20, y));
            var t = Ui.T(name, 160, y, 320);
            t.AccessibleName = label;
            p.Controls.Add(t);
            return t;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; Accept(); return; }
            if (e.KeyCode == Keys.Escape) { Clear(); return; }
            if (e.KeyCode == Keys.Enter && order.Contains(ActiveControl))
            {
                e.SuppressKeyPress = true;
                int i = order.IndexOf(ActiveControl);
                order[(i + 1) % order.Count].Focus();
            }
        }

        void Accept()
        {
            DateTime d;
            if (!DateTime.TryParseExact(date.Text.Trim(), new[] { "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy" },
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            { MessageBox.Show(this, "Invalid date. Use dd-mm-yyyy.", "BooksDesk"); Journal.Write("books", "refused", Journal.D("why", "date", "value", date.Text)); return; }
            var amt = Ui.Money(amount.Text);
            if (amt == null || amt <= 0 || party.Text.Trim() == "")
            { MessageBox.Show(this, "Party and a positive amount are needed.", "BooksDesk"); Journal.Write("books", "refused", Journal.D("why", "amount or party")); return; }
            foreach (DataGridViewRow r in daybook.Rows)
            {
                if (Convert.ToString(r.Cells[0].Value) == (string)type.SelectedItem && string.Equals(Convert.ToString(r.Cells[2].Value), party.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Convert.ToString(r.Cells[3].Value), refNo.Text.Trim(), StringComparison.OrdinalIgnoreCase) && refNo.Text.Trim() != "")
                {
                    MessageBox.Show(this, "Duplicate voucher: " + party.Text + " " + refNo.Text + " is already entered.", "BooksDesk");
                    Journal.Write("books", "duplicate_refused", Journal.D("party", party.Text, "ref", refNo.Text));
                    return;
                }
            }
            daybook.Rows.Add(type.SelectedItem, d.ToString("dd-MM-yyyy"), party.Text.Trim(), refNo.Text.Trim(), amt.Value.ToString("0.00", CultureInfo.InvariantCulture), gst.Text.Trim(), narration.Text.Trim());
            Journal.Write("books", "voucher_saved", Journal.D("type", type.SelectedItem, "date", d.ToString("yyyy-MM-dd"), "party", party.Text.Trim(),
                "ref", refNo.Text.Trim(), "amount", amt.Value, "gst", gst.Text.Trim(), "narration", narration.Text.Trim()));
            status.Text = "Voucher accepted: " + party.Text.Trim() + " " + amt.Value.ToString("0.00", CultureInfo.InvariantCulture);
            Clear();
        }

        void Clear()
        {
            foreach (var t in new[] { date, party, refNo, amount, gst, narration }) t.Text = "";
            type.Focus();
        }

        void MarkReceived()
        {
            if (invoices.CurrentRow == null) return;
            var row = invoices.CurrentRow;
            if (Convert.ToString(row.Cells[3].Value) == "Received") { MessageBox.Show(this, "Already received.", "BooksDesk"); return; }
            using (var f = new Form { Text = "Receipt reference", Width = 360, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent })
            {
                var box = Ui.T("ReceiptRef", 20, 20, 300);
                box.AccessibleName = "Receipt reference";
                var ok = Ui.B("OK", "OK", 20, 60, 90);
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(box); f.Controls.Add(ok); f.AcceptButton = ok;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                row.Cells[3].Value = "Received";
                row.Cells[4].Value = box.Text.Trim();
                Journal.Write("books", "invoice_received", Journal.D("invoice", row.Cells[0].Value, "ref", box.Text.Trim()));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // StockKeeper: inventory
    // ──────────────────────────────────────────────────────────────────
    class StockKeeper : Form
    {
        readonly DataGridView grid;
        readonly ComboBox warehouse = new ComboBox { Name = "Warehouse", DropDownStyle = ComboBoxStyle.DropDownList, Left = 100, Top = 12, Width = 160, AccessibleName = "Warehouse" };
        readonly TextBox search;
        readonly Label loading = new Label { Name = "Loading", Text = "", Left = 600, Top = 16, AutoSize = true, ForeColor = Color.DarkOrange };
        readonly Dictionary<string, List<object[]>> stock = new Dictionary<string, List<object[]>>();
        bool lockedOnce;

        public StockKeeper(Dictionary<string, object> data, string title)
        {
            Text = string.IsNullOrEmpty(title) ? "StockKeeper" : title;
            Width = 900; Height = 620;
            foreach (var o in (ArrayList)data["stock"])
            {
                var r = (ArrayList)o;
                string w = (string)r[0];
                if (!stock.ContainsKey(w)) { stock[w] = new List<object[]>(); warehouse.Items.Add(w); }
                stock[w].Add(new object[] { r[1], r[2], Convert.ToInt32(r[3]), Convert.ToInt32(r[4]) });
            }
            Controls.Add(Ui.L("Warehouse", 12, 12)); Controls.Add(warehouse);
            search = Ui.T("Search", 340, 12, 160); search.AccessibleName = "Search SKU";
            Controls.Add(Ui.L("SKU", 300, 12)); Controls.Add(search);
            var find = Ui.B("Find", "Find", 510, 10, 70);
            find.Click += delegate { Find(); };
            Controls.Add(find); Controls.Add(loading);
            grid = Ui.Grid("Stock", 12, 50, 860, 440, "SKU", "Item", "Qty", "Reorder Level");
            Controls.Add(grid);
            var adjust = Ui.B("Adjust", "&Adjust stock...", 12, 500, 140);
            adjust.Click += delegate { Adjust(); };
            var low = Ui.B("LowStock", "&Low stock report", 160, 500, 150);
            low.Click += delegate { LowStock(); };
            Controls.Add(adjust); Controls.Add(low);
            warehouse.SelectedIndexChanged += delegate { Refresh(false); };
            if (warehouse.Items.Count > 0) warehouse.SelectedIndex = 0;
        }

        List<object[]> Rows { get { return stock[(string)warehouse.SelectedItem]; } }

        void Refresh(bool slow)
        {
            if (slow)
            {
                grid.Enabled = false; loading.Text = "Loading...";
                var t = new Timer { Interval = 1500 };
                t.Tick += delegate { t.Stop(); Fill(); grid.Enabled = true; loading.Text = ""; };
                t.Start();
            }
            else Fill();
        }

        void Fill()
        {
            grid.Rows.Clear();
            foreach (var r in Rows) grid.Rows.Add(r[0], r[1], r[2], r[3]);
        }

        void Find()
        {
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (string.Equals(Convert.ToString(r.Cells[0].Value), search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                { grid.CurrentCell = r.Cells[0]; r.Selected = true; return; }
            }
            MessageBox.Show(this, "No SKU " + search.Text + " in " + warehouse.SelectedItem + ".", "StockKeeper");
        }

        void Adjust()
        {
            if (grid.CurrentRow == null || !grid.Enabled) return;
            string sku = Convert.ToString(grid.CurrentRow.Cells[0].Value);
            if (sku == "UK-LMP-01" && !lockedOnce)
            {
                lockedOnce = true;
                Journal.Write("stock", "record_locked", Journal.D("sku", sku));
                MessageBox.Show(this, "Record is in use by another user. Try again in a moment.", "StockKeeper");
                return;
            }
            using (var f = new Form { Text = "Adjust stock: " + sku, Width = 380, Height = 220, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent })
            {
                var change = Ui.T("Change", 140, 20, 180); change.AccessibleName = "Change (+/-)";
                var reason = new ComboBox { Name = "Reason", Left = 140, Top = 60, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Reason" };
                reason.Items.AddRange(new object[] { "Sale", "Return", "Damage", "Count correction", "Transfer" });
                reason.SelectedIndex = 0;
                var ok = Ui.B("Apply", "Apply", 140, 110, 90); ok.DialogResult = DialogResult.OK;
                f.Controls.Add(Ui.L("Change (+/-)", 20, 20)); f.Controls.Add(change);
                f.Controls.Add(Ui.L("Reason", 20, 60)); f.Controls.Add(reason); f.Controls.Add(ok); f.AcceptButton = ok;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int n;
                if (!int.TryParse(change.Text.Trim().Replace("+", ""), out n) || n == 0) { MessageBox.Show(this, "Enter a whole number, e.g. -2 or +5.", "StockKeeper"); return; }
                if (MessageBox.Show(this, "Change " + sku + " by " + n + " (" + reason.SelectedItem + ")?", "Are you sure?", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                var row = Rows.First(r => (string)r[0] == sku);
                int before = (int)row[2];
                row[2] = Math.Max(0, before + n);
                Journal.Write("stock", "adjusted", Journal.D("warehouse", warehouse.SelectedItem, "sku", sku, "change", n, "reason", reason.SelectedItem, "before", before, "after", row[2]));
                Refresh(true);
            }
        }

        void LowStock()
        {
            using (var f = new Form { Text = "Low stock report - " + warehouse.SelectedItem, Width = 640, Height = 460, StartPosition = FormStartPosition.CenterParent })
            {
                var g = Ui.Grid("LowStockReport", 10, 10, 600, 340, "SKU", "Item", "Qty", "Reorder Level", "Short By");
                foreach (var r in Rows.Where(r => (int)r[2] < (int)r[3])) g.Rows.Add(r[0], r[1], r[2], r[3], (int)r[3] - (int)r[2]);
                var ex = Ui.B("ExportReport", "&Export...", 10, 360, 120);
                ex.Click += delegate { Ui.Save(f, "low_stock.csv", Ui.Csv(g), "stock"); };
                f.Controls.Add(g); f.Controls.Add(ex);
                f.ShowDialog(this);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // PayrollPro
    // ──────────────────────────────────────────────────────────────────
    class PayrollPro : Form
    {
        readonly DataGridView employees, timesheets, payroll;
        readonly ComboBox month = new ComboBox { Name = "Month", DropDownStyle = ComboBoxStyle.DropDownList, Left = 80, Top = 12, Width = 140, AccessibleName = "Month" };

        public PayrollPro(Dictionary<string, object> data, string title)
        {
            Text = string.IsNullOrEmpty(title) ? "PayrollPro" : title;
            Width = 960; Height = 640;
            var tabs = new TabControl { Name = "Tabs", Dock = DockStyle.Fill };
            var emp = new TabPage("Employees") { Name = "pgEmployees" };
            employees = Ui.Grid("Employees", 10, 10, 920, 470, "Code", "Name", "PAN", "IFSC", "Account", "Joined", "Monthly Salary", "Department");
            foreach (var o in (ArrayList)data["employees"]) employees.Rows.Add(((ArrayList)o).ToArray());
            var add = Ui.B("NewEmployee", "&New employee...", 10, 490, 150);
            add.Click += delegate { NewEmployee(); };
            emp.Controls.Add(employees); emp.Controls.Add(add);

            var ts = new TabPage("Timesheets") { Name = "pgTimesheets" };
            timesheets = Ui.Grid("Timesheets", 10, 10, 920, 470, "Code", "Date", "Hours", "Leave");
            var imp = Ui.B("ImportCsv", "&Import CSV...", 10, 490, 140);
            imp.Click += delegate { Import(); };
            ts.Controls.Add(timesheets); ts.Controls.Add(imp);

            var pay = new TabPage("Payroll") { Name = "pgPayroll" };
            month.Items.AddRange(new object[] { "October 2026", "November 2026" }); month.SelectedIndex = 1;
            pay.Controls.Add(Ui.L("Month", 10, 12)); pay.Controls.Add(month);
            var run = Ui.B("RunPayroll", "&Run payroll", 240, 10, 120);
            run.Click += delegate { Run(); };
            payroll = Ui.Grid("PayrollResult", 10, 50, 920, 430, "Code", "Name", "Days Worked", "Overtime Hours", "Gross", "Deductions", "Net Pay");
            var ex = Ui.B("ExportPayroll", "&Export...", 10, 490, 120);
            ex.Click += delegate { Ui.Save(this, "payroll_" + ((string)month.SelectedItem).Replace(" ", "_") + ".csv", Ui.Csv(payroll), "payroll"); };
            pay.Controls.Add(run); pay.Controls.Add(payroll); pay.Controls.Add(ex);
            tabs.TabPages.Add(emp); tabs.TabPages.Add(ts); tabs.TabPages.Add(pay);
            Controls.Add(tabs);
        }

        void NewEmployee()
        {
            using (var f = new Form { Text = "New employee", Width = 460, Height = 420, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent })
            {
                string[] labels = { "Employee code", "Full name", "PAN", "Bank IFSC", "Account number", "Date of joining (dd-mm-yyyy)", "Monthly salary" };
                var boxes = new List<TextBox>();
                for (int i = 0; i < labels.Length; i++)
                {
                    f.Controls.Add(Ui.L(labels[i], 20, 20 + i * 40));
                    var t = Ui.T("Emp" + i, 220, 20 + i * 40, 200); t.AccessibleName = labels[i];
                    f.Controls.Add(t); boxes.Add(t);
                }
                var dept = new ComboBox { Name = "Department", Left = 220, Top = 300, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Department" };
                dept.Items.AddRange(new object[] { "Delivery", "Recruitment", "Finance", "Operations" }); dept.SelectedIndex = 0;
                f.Controls.Add(Ui.L("Department", 20, 300)); f.Controls.Add(dept);
                var save = Ui.B("SaveEmployee", "Save", 220, 340, 90);
                f.Controls.Add(save); f.AcceptButton = save;
                save.Click += delegate
                {
                    if (!Regex.IsMatch(boxes[2].Text.Trim().ToUpperInvariant(), "^[A-Z]{5}[0-9]{4}[A-Z]$"))
                    { MessageBox.Show(f, "PAN must be 5 letters, 4 digits and a letter.", "PayrollPro"); Journal.Write("payroll", "refused", Journal.D("why", "pan", "value", boxes[2].Text)); return; }
                    foreach (DataGridViewRow r in employees.Rows)
                        if (string.Equals(Convert.ToString(r.Cells[0].Value), boxes[0].Text.Trim(), StringComparison.OrdinalIgnoreCase))
                        { MessageBox.Show(f, "Employee code " + boxes[0].Text + " exists.", "PayrollPro"); Journal.Write("payroll", "duplicate_refused", Journal.D("code", boxes[0].Text)); return; }
                    employees.Rows.Add(boxes[0].Text.Trim(), boxes[1].Text.Trim(), boxes[2].Text.Trim().ToUpperInvariant(), boxes[3].Text.Trim().ToUpperInvariant(), boxes[4].Text.Trim(), boxes[5].Text.Trim(), boxes[6].Text.Trim(), dept.SelectedItem);
                    Journal.Write("payroll", "employee_added", Journal.D("code", boxes[0].Text.Trim(), "name", boxes[1].Text.Trim(), "pan", boxes[2].Text.Trim().ToUpperInvariant(),
                        "ifsc", boxes[3].Text.Trim().ToUpperInvariant(), "salary", boxes[6].Text.Trim(), "department", dept.SelectedItem));
                    f.DialogResult = DialogResult.OK;
                };
                f.ShowDialog(this);
            }
        }

        void Import()
        {
            using (var dlg = new OpenFileDialog { Title = "Import timesheets", Filter = "CSV files (*.csv)|*.csv|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var lines = File.ReadAllLines(dlg.FileName).Skip(1).Where(l => l.Trim() != "").ToList();
                foreach (var l in lines) timesheets.Rows.Add(l.Split(',').Select(x => (object)x.Trim()).ToArray());
                Journal.Write("payroll", "timesheets_imported", Journal.D("file", dlg.FileName, "rows", lines.Count));
            }
        }

        void Run()
        {
            payroll.Rows.Clear();
            foreach (DataGridViewRow e in employees.Rows)
            {
                string code = Convert.ToString(e.Cells[0].Value);
                var mine = timesheets.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToString(r.Cells[0].Value) == code).ToList();
                decimal salary = Ui.Money(Convert.ToString(e.Cells[6].Value)) ?? 0;
                int days = mine.Count(r => Convert.ToString(r.Cells[3].Value) == "");
                int lop = mine.Count(r => Convert.ToString(r.Cells[3].Value) == "LOP");
                decimal ot = mine.Sum(r => Math.Max(0, (Ui.Money(Convert.ToString(r.Cells[2].Value)) ?? 0) - 9));
                decimal daily = salary / 30m;
                decimal gross = Math.Round(salary + ot * daily / 9m * 1.5m, 2);
                decimal deduct = Math.Round(lop * daily + gross * 0.12m, 2);
                payroll.Rows.Add(code, e.Cells[1].Value, days, ot, gross.ToString("0.00", CultureInfo.InvariantCulture), deduct.ToString("0.00", CultureInfo.InvariantCulture), (gross - deduct).ToString("0.00", CultureInfo.InvariantCulture));
            }
            Journal.Write("payroll", "payroll_run", Journal.D("month", month.SelectedItem, "employees", payroll.Rows.Count));
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string app = "", dataFile = "", title = "";
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--app") app = args[++i];
                else if (args[i] == "--data") dataFile = args[++i];
                else if (args[i] == "--log") Journal.Path = args[++i];
                else if (args[i] == "--title") title = args[++i];
            }
            var data = new Dictionary<string, object>();
            if (dataFile != "" && File.Exists(dataFile))
                data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(dataFile, Encoding.UTF8));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Form f;
            switch (app)
            {
                case "books": f = new BooksDesk(data, title); break;
                case "stock": f = new StockKeeper(data, title); break;
                case "payroll": f = new PayrollPro(data, title); break;
                default: MessageBox.Show("--app books|stock|payroll"); return 2;
            }
            Journal.Write(app, "started", Journal.D("title", f.Text));
            Application.Run(f);
            return 0;
        }
    }
}
