#!/usr/bin/env python3
"""印出目前設定對應到哪個 preset（比對時忽略 activity 與 ui）。沒有對應就不印。"""
import glob, json, os, sys

def norm(path):
    """回傳 dict 而不是 json 字串：字串比對會因為 1 與 1.0 序列化不同而誤判不一致。"""
    try:
        o = json.load(open(path))
    except Exception:
        return None
    o.get("motion", {}).pop("activity", None)
    o.pop("ui", None)
    return o

here = os.path.dirname(os.path.abspath(__file__))
cur = norm(os.path.expanduser("~/.config/wall42/config.json"))
if cur is None:
    sys.exit(0)
for f in sorted(glob.glob(os.path.join(here, "presets", "*.json"))):
    if norm(f) == cur:
        print(os.path.basename(f)[:-5])
        break
