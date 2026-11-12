"""Small changes to the practice world, a different set each night: what real sites do between one run and the
next. Bots built on night 0 run against them unattended; how many still finish is the benchmark's score.

    drift(root, night)   rewrites every page under ``root`` (world.build's) for that night; night 0 = none

Each night turns on some of (always the same for the same night):

* ``labels``  buttons and links say it another way ("Search" -> "Find", "Next" -> "Next page", "Download" -> "Get")
* ``columns`` a table's third column moved to its end (its header and every row)
* ``slow``    the page shows 0.8-3 s after it loads
* ``banner``  a cookie notice over the top of the page until "Accept" is pressed
* ``extra``   a new column ("Updated") added at a table's end

The changes are made in the browser by one script each page loads (``_drift.js``), so the sites' own scripts and data
stay as they are and the judge's expected outputs do not move.
"""
from __future__ import annotations

import json
import random
from pathlib import Path
from typing import Dict, List

KINDS = ("labels", "columns", "slow", "banner", "extra")

#: A button's or link's words, said another way.
LABELS = {"Search": "Find", "Next": "Next page", "Previous": "Previous page", "Download": "Get", "Submit": "Send",
          "Sign in": "Log in", "Find": "Look up", "Add candidate": "Save candidate", "Acknowledge": "Confirm",
          "Check warranty": "Look up warranty", "View": "Open", "Book shipment": "Create shipment",
          "Track": "Track shipment"}

SCRIPT = r"""(function(){var C=%s;
function relabel(root){if(!C.labels)return;(root.querySelectorAll?root:document).querySelectorAll('button,a,input[type=submit]').forEach(function(b){
 var t=(b.value&&b.tagName=='INPUT')?b.value:b.textContent;var k=(t||'').trim();if(C.labels[k]){if(b.tagName=='INPUT')b.value=C.labels[k];else b.textContent=C.labels[k];}});}
function columns(){document.querySelectorAll('table').forEach(function(t){if(t.dataset.drift)return;var rows=t.querySelectorAll('tr');if(!rows.length)return;
 var n=rows[0].children.length;if(C.columns&&n>=4){rows.forEach(function(r){if(r.children.length==n)r.appendChild(r.children[2]);});}
 if(C.extra&&n>=2){rows.forEach(function(r,i){var c=document.createElement(i==0&&r.children[0].tagName=='TH'?'th':'td');c.textContent=i==0?'Updated':'2026-11-0'+(1+i%%7);r.appendChild(c);});}
 t.dataset.drift='1';});}
function apply(){relabel(document);columns();}
if(C.slow){document.documentElement.style.visibility='hidden';}
document.addEventListener('DOMContentLoaded',function(){
 if(C.banner){var d=document.createElement('div');d.id='cookie-notice';d.setAttribute('role','dialog');
  d.style.cssText='position:fixed;top:0;left:0;right:0;background:#333;color:#fff;padding:14px;z-index:9999';
  d.innerHTML='We use cookies to keep you signed in. <button id="cookie-accept">Accept</button>';document.body.appendChild(d);
  document.getElementById('cookie-accept').onclick=function(){d.remove();};}
 apply();new MutationObserver(function(){relabel(document);}).observe(document.body,{childList:true,subtree:true});
 if(C.slow){setTimeout(function(){document.documentElement.style.visibility='';},C.slow);}});
})();"""


def plan(night: int) -> Dict[str, object]:
    """What changes on ``night`` (the same each time it is asked): night 0 changes nothing; every other night two or
    three of the kinds, every kind on at least one night of the first five."""
    if night <= 0:
        return {}
    rng = random.Random(7919 * night)
    first = KINDS[(night - 1) % len(KINDS)]
    chosen = [first] + rng.sample([k for k in KINDS if k != first], rng.choice([1, 2]))
    out: Dict[str, object] = {}
    for k in chosen:
        if k == "labels":
            keys = sorted(LABELS)
            out["labels"] = {key: LABELS[key] for key in rng.sample(keys, max(3, len(keys) // 2))}
        elif k == "slow":
            out["slow"] = rng.randrange(800, 3000, 100)
        else:
            out[k] = True
    return out


def drift(root: Path, night: int) -> Dict[str, object]:
    """Rewrite every page under ``root`` for ``night`` (idempotent: the script tag goes in once)."""
    root = Path(root)
    chosen = plan(night)
    (root / "_drift.js").write_text(SCRIPT % json.dumps(chosen, ensure_ascii=False), encoding="utf-8")
    tag = "<script src='/_drift.js'></script>"
    for page in root.rglob("*.html"):
        text = page.read_text(encoding="utf-8")
        if tag in text:
            continue
        # First in the head: the slow page is hidden before anything shows.
        text = text.replace("<head>", "<head>" + tag, 1) if "<head>" in text else tag + text
        page.write_text(text, encoding="utf-8")
    return chosen


def summary(nights: List[int]) -> List[str]:
    return [f"night {n}: {', '.join(sorted(plan(n))) or 'no change'}" for n in nights]
