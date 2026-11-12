# Celers RPA Benchmark

**Do your bots keep working when the sites change?** A bot that passes on the day it is built can fail the next
week because a button was renamed, a column moved or a cookie notice appeared. This benchmark measures exactly that,
for any automation tool: UiPath, Power Automate, Automation Anywhere, Selenium scripts, AI agents, or
[Celers](https://celers.org).

Made and maintained by [Celers Automation](https://celers.org). Apache 2.0.

## What is in it

- **Fourteen practice websites** that run on your own computer: a bank, a GST portal, a seller central with a
  competitor's shop, a courier, a product-warranty partner portal, a recruiting site and more. Everything is made
  up; nothing reaches the internet. They have what real sites have: one-time passwords, sessions that time out,
  paged tables, an error page on the first visit, pop-ups at random, a rate limit, closed shadow DOM, an iframe form,
  captchas a bot must not solve.
- **Practice desktop apps** (`PracticeApps.exe`: PayrollPro, BooksDesk, StockKeeper), Windows Forms,
  each writing a journal of what was done in it.
- **The Remote Lab**: a session host and a workspace client that work like Citrix: the apps run in a session of
  their own and only a picture of them reaches the bot, with lag, jitter and resolution changes if you want them.
- **Twelve tasks** with exact expected results, and a **judge** that scores what a bot wrote (files and journal),
  never how it did it.
- **Nights**: night 0 is the sites as they are, the night you build your bots. Each night after changes them in
  two or three of five ways: buttons and links worded differently, a table's column moved, a new column, slow
  pages, a cookie banner. The data stays the same, so the expected results do too.

## How to run it

Python 3.9 or later (no packages needed). The desktop apps need Windows and its built-in .NET Framework 4.

```
python build.py                                     # Windows: the desktop apps and the Remote Lab into bin/
python -m benchmark setup --run runs/night0         # a run folder: the sites as on night 0, inputs, TASKS.md
python -m benchmark serve --run runs/night0         # the websites at http://127.0.0.1:8780/
python -m benchmark apps  --run runs/night0         # PayrollPro, for the desktop tasks
python -m benchmark lab   --run runs/night0 --latency 300 --jitter 200   # PayrollPro in a remote session
# ... build your bots from TASKS.md, run them, they write to runs/night0/out/
python -m benchmark judge --run runs/night0         # PASS/FAIL per task, results.json
```

Then, without changing the bots, run them on the nights after:

```
python -m benchmark setup --run runs/night1 --night 1
python -m benchmark serve --run runs/night1
python -m benchmark judge --run runs/night1
python -m benchmark nights                          # what changes on each night
```

**The score** is how many of the twelve tasks still pass on nights 1 to 14 with the bots built on night 0, with
no one touching them. Report it as a table: one row per night.

## The tasks

| Task | Where | What makes it hard |
|---|---|---|
| bank-statements | BankLite | one-time password, a session that times out |
| gst-status | LedgerTax | two pages, 10-minute session |
| orders | Seller Central | an error page on the first visit, a pop-up at random, three pages |
| prices | Seller Central + HomeMart | two sites, product cards instead of a table, prices written differently |
| tracking | ShipTrack | a rate limit of 20 lookups a minute, a result that appears late |
| rejected-claims | WarrantyHub | a table drawn by script with its own pager, details one page deeper |
| warranty-check | WarrantyHub | a closed shadow root, serial numbers written three ways |
| candidates | HireBoard | duplicates, captchas a bot must not solve, a view limit |
| payroll-joiners | PayrollPro | a desktop app, validation to respect (one wrong PAN, one code that exists) |
| payroll-run | PayrollPro | a file dialog, three tabs |
| remote-payroll-joiners | PayrollPro in the Remote Lab | only a picture of the app, lag |
| remote-payroll-run | PayrollPro in the Remote Lab | only a picture of the app, lag |

The full instructions, with the addresses and folders of your run, are in each run's `TASKS.md`.

## Rules

- Build and test your bots on night 0 only. On the nights after, run them as they are.
- A captcha is never solved by a bot; a refused entry is never forced in. The judge fails a task that does.
- Publish your tool's name and version, the nights, and `results.json` for each night.

## Results

| Tool | Nights | Tasks passed per night |
|---|---|---|
| Celers | to come | |

Send yours as a pull request.

## About

Celers builds automations from a description in plain words, for websites, desktop apps and Citrix, and keeps them
running. See [celers.org](https://celers.org).
