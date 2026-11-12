"""Build the benchmark's desktop programs into bin/ with the C# compiler that ships with Windows (.NET Framework 4):

    python build.py

* PracticeApps.exe     the practice desktop apps (PayrollPro, BooksDesk, StockKeeper)
* SessionHost.exe      the Remote Lab's server: runs the apps in a session of their own, sends only pictures
* CelersWorkspace.exe  the Remote Lab's client: shows the session's windows, passes the mouse and keys back
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
CSC = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
REFS = ["System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Web.Extensions.dll"]
MANIFEST = HERE / "desktop" / "app.manifest"
PROGRAMS = {
    "PracticeApps.exe": ([HERE / "desktop" / "PracticeApps.cs"], "winexe"),
    "SessionHost.exe": ([HERE / "remotelab" / "SessionHost.cs", HERE / "remotelab" / "Wire.cs"], "exe"),
    "CelersWorkspace.exe": ([HERE / "remotelab" / "Workspace.cs", HERE / "remotelab" / "Wire.cs"], "winexe"),
}


def main() -> int:
    if not CSC.is_file():
        raise SystemExit(f"No C# compiler at {CSC}: it comes with .NET Framework 4 on Windows.")
    BIN.mkdir(exist_ok=True)
    for name, (sources, kind) in PROGRAMS.items():
        cmd = [str(CSC), "/nologo", f"/target:{kind}", "/optimize+", f"/out:{BIN / name}",
               f"/win32manifest:{MANIFEST}", "/r:" + ",".join(REFS)] + [str(s) for s in sources]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout + r.stderr)
            raise SystemExit(f"{name} did not build")
        print(f"built {BIN / name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
