"""The Celers RPA Benchmark's commands.

    python -m benchmark setup  --run RUN [--night N] [--port 8780]   a run folder: the sites as on night N, inputs, TASKS.md
    python -m benchmark serve  --run RUN                             the practice websites at http://127.0.0.1:PORT/
    python -m benchmark apps   --run RUN [--app payroll]             a practice desktop app, writing its journal
    python -m benchmark lab    --run RUN [--latency MS --jitter MS]  the same app in a remote session (the Remote Lab)
    python -m benchmark judge  --run RUN [--task ID ...]             score the run's out/ folder (results.json)
    python -m benchmark tasks                                        list the tasks
    python -m benchmark nights [--to 14]                             what changes on each night
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from . import drift, tasks, world
from .judge import judge

HERE = Path(__file__).resolve().parent
BIN = HERE.parent / "bin"


def _run_meta(run: Path) -> dict:
    path = run / "run.json"
    if not path.is_file():
        raise SystemExit(f"{run} is not a run folder: python -m benchmark setup --run {run}")
    return json.loads(path.read_text(encoding="utf-8"))


def cmd_setup(a) -> int:
    run = Path(a.run).resolve()
    if (run / "out").exists() and any((run / "out").iterdir()) and not a.force:
        raise SystemExit(f"{run}/out already has results: use another folder, or --force")
    site = run / "world"
    world.build(site)
    changes = drift.drift(site, a.night)
    tasks.write_inputs(run / "inputs")
    (run / "out" / "statements").mkdir(parents=True, exist_ok=True)
    url = f"http://127.0.0.1:{a.port}/"
    meta = {"night": a.night, "port": a.port, "url": url, "changes": changes,
            "made": datetime.now(timezone.utc).isoformat(timespec="seconds")}
    (run / "run.json").write_text(json.dumps(meta, indent=1), encoding="utf-8")
    lines = [f"# Celers RPA Benchmark: night {a.night}", "",
             f"Sites: {url} (`python -m benchmark serve --run {run}`). What changed tonight: "
             f"{', '.join(sorted(changes)) or 'nothing (the night bots are built on)'}.", ""]
    for t in tasks.TASKS.values():
        ask = t.ask.format(url=url, inputs=str(run / "inputs") + "\\", out=str(run / "out") + "\\")
        lines += [f"## {t.id}: {t.title} ({t.kind})", "", ask, "", f"What makes it hard: {', '.join(t.hard)}.", ""]
    (run / "TASKS.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"run folder {run}: night {a.night}, {len(tasks.TASKS)} tasks in TASKS.md")
    return 0


def cmd_serve(a) -> int:
    import functools
    import http.server

    meta = _run_meta(Path(a.run))
    root = Path(a.run).resolve() / "world"
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(root))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", meta["port"]), handler)
    print(f"practice websites at {meta['url']} (night {meta['night']}); Ctrl+C stops", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


def _exe(name: str) -> Path:
    exe = BIN / name
    if not exe.is_file():
        raise SystemExit(f"{exe} is missing: build the desktop programs with python build.py (Windows)")
    return exe


def _app_args(run: Path, app: str, journal: str) -> list:
    data = run / "inputs" / f"{app}_data.json"
    return ["--app", app, "--title", {"payroll": "PayrollPro"}.get(app, app), "--log", str(run / journal)] + (
        ["--data", str(data)] if data.is_file() else [])


def cmd_apps(a) -> int:
    run = Path(a.run).resolve()
    _run_meta(run)
    subprocess.Popen([str(_exe("PracticeApps.exe"))] + _app_args(run, a.app, "journal.jsonl"))
    print(f"{a.app} started; its journal: {run / 'journal.jsonl'}")
    return 0


def cmd_lab(a) -> int:
    run = Path(a.run).resolve()
    _run_meta(run)
    args = " ".join(f'"{x}"' if " " in x else x for x in _app_args(run, a.app, "remote_journal.jsonl"))
    host = [str(_exe("SessionHost.exe")), "--port", str(a.lab_port), "--latency", str(a.latency),
            "--jitter", str(a.jitter), "--res-every", str(a.res_every),
            "--app", f"PayrollPro={_exe('PracticeApps.exe')}|{args}"]
    subprocess.Popen(host)
    subprocess.Popen([str(_exe("CelersWorkspace.exe")), "--server", f"127.0.0.1:{a.lab_port}", "--user", "celers",
                      "--password", "lab", "--auto", "--launch", "PayrollPro"])
    print(f"Remote Lab: session host on port {a.lab_port} ({a.latency} ms lag, {a.jitter} ms jitter); "
          f"PayrollPro opens in the workspace. Journal: {run / 'remote_journal.jsonl'}")
    return 0


def cmd_judge(a) -> int:
    report = judge(Path(a.run).resolve(), a.task or None)
    width = max(len(t) for t in report["results"])
    for tid, r in report["results"].items():
        print(f"{'PASS' if r['pass'] else 'FAIL'}  {tid:<{width}}  {r['right']}/{r['expected']}  {r['why']}")
    print(f"\nnight {report['night']}: {report['passed']} of {report['tasks']} tasks passed "
          f"(results.json in the run folder)")
    return 0 if report["passed"] == report["tasks"] else 1


def cmd_tasks(_a) -> int:
    for t in tasks.TASKS.values():
        print(f"{t.id:<26} {t.kind:<8} {t.title}")
    return 0


def cmd_nights(a) -> int:
    print("\n".join(drift.summary(list(range(0, a.to + 1)))))
    return 0


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="python -m benchmark", description="The Celers RPA Benchmark (celers.org)")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("setup")
    s.add_argument("--run", required=True)
    s.add_argument("--night", type=int, default=0)
    s.add_argument("--port", type=int, default=8780)
    s.add_argument("--force", action="store_true")
    s.set_defaults(func=cmd_setup)
    for name, func in (("serve", cmd_serve), ("apps", cmd_apps), ("lab", cmd_lab), ("judge", cmd_judge)):
        c = sub.add_parser(name)
        c.add_argument("--run", required=True)
        if name in ("apps", "lab"):
            c.add_argument("--app", default="payroll")
        if name == "lab":
            c.add_argument("--lab-port", type=int, default=7480)
            c.add_argument("--latency", type=int, default=0)
            c.add_argument("--jitter", type=int, default=0)
            c.add_argument("--res-every", type=int, default=0)
        if name == "judge":
            c.add_argument("--task", action="append")
        c.set_defaults(func=func)
    sub.add_parser("tasks").set_defaults(func=cmd_tasks)
    n = sub.add_parser("nights")
    n.add_argument("--to", type=int, default=14)
    n.set_defaults(func=cmd_nights)
    a = p.parse_args(argv)
    return a.func(a)


if __name__ == "__main__":
    sys.exit(main())
