#!/usr/bin/env -S uv run --script --quiet
# /// script
# requires-python = ">=3.10"
# dependencies = ["mcp>=2,<3"]
# ///
"""wall42 MCP server：讓 AI 查詢並操控桌布層動態粒子。

控制介面就是設定檔本身 —— wall42 每秒檢查 config.json 的 mtime，
改完 1 秒內自動套用，所以這裡不需要任何 IPC，寫檔就好。

跟 cool42 的關係：cool42 管風扇與溫度（要不要開工），
wall42 管的是「讓人看得出機器在忙」。兩者都讀系統負載但互不依賴。

同一支檔案支援 macOS 與 Windows（sys.platform 分支）：
- macOS：~/.config/wall42（設定、.signal、sessions.json）、~/Library/Logs/wall42.log、launchctl。
- Windows：%APPDATA%\\wall42（config.json、.signal、.sync-request、sessions.json），
  %LOCALAPPDATA%\\wall42（wall42.log、status.json、桌布 PNG）；即時狀態讀 status.json 而不是 log；
  start/stop 用安裝的 wall42.exe（WALL42_EXE 可指定）。
  WALL42_CONFIG／WALL42_SIGNAL_DIR／WALL42_LOG 可把三個位置搬去別處（測試用，跟 wall42.exe 的同名變數一致）。
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from typing import Any, Literal

from mcp.server.mcpserver import MCPServer
# 只有 ToolError 的訊息會原樣傳給 AI，其他例外只剩「Error executing tool」
from mcp.server.mcpserver.exceptions import ToolError

IS_WIN = sys.platform == "win32"
HOME = os.path.expanduser("~")
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if IS_WIN:
    _ROAMING = os.environ.get("APPDATA") or os.path.join(HOME, "AppData", "Roaming")
    _LOCAL = os.environ.get("LOCALAPPDATA") or os.path.join(HOME, "AppData", "Local")
    # 控制信號固定在 %APPDATA%\wall42，不跟著 WALL42_CONFIG 漂移（同 Mac 的理由）
    SIGNAL_DIR = os.environ.get("WALL42_SIGNAL_DIR") or os.path.join(_ROAMING, "wall42")
    CONFIG = os.environ.get("WALL42_CONFIG") or os.path.join(_ROAMING, "wall42", "config.json")
    LOG = os.environ.get("WALL42_LOG") or os.path.join(_LOCAL, "wall42", "wall42.log")
    STATE_DIR = os.path.dirname(os.path.abspath(LOG))
    STATUS = os.path.join(STATE_DIR, "status.json")
else:
    CONFIG = os.path.join(HOME, ".config/wall42/config.json")
    LOG = os.path.join(HOME, "Library/Logs/wall42.log")
    SIGNAL_DIR = os.path.join(HOME, ".config/wall42")
# presets 跟著這支腳本所在的 repo 走，repo 搬家或改名都不會斷
PRESETS = os.path.join(REPO, "presets")
if IS_WIN and not os.path.isdir(PRESETS):
    PRESETS = os.path.join(_LOCAL, "wall42", "presets")      # 安裝版的副本
LABEL = "com.kang.wall42"
INSTALL_HINT = "wall42 可能沒安裝或還沒啟動過" if IS_WIN else "wall42 可能沒安裝，跑 ./install.sh"

mcp = MCPServer(
    "wall42",
    instructions=(
        "wall42 是桌布層動態粒子，被視窗完全遮擋時會停止繪製。"
        "跑長任務前用 wall42_think 讓畫面忙起來（到期自動回復，不必記得關）；"
        "真的想通一件事的當下用 wall42_insight 閃一下。"
        "要永久改忙碌度來源才用 wall42_set_activity。"
        "改設定都是寫 config.json，wall42 會在 1 秒內自動套用。"
    ),
)


def _read_config() -> dict[str, Any]:
    try:
        # utf-8-sig：Windows 上用記事本存檔可能帶 BOM；macOS 讀起來結果不變
        with open(CONFIG, encoding="utf-8-sig") as f:
            return json.load(f)
    except FileNotFoundError:
        raise ToolError(f"設定檔不存在：{CONFIG}（{INSTALL_HINT}）")
    except json.JSONDecodeError as e:
        raise ToolError(f"設定檔不是合法 JSON：{e}")


def _replace(tmp: str, dst: str) -> None:
    """原子替換。Windows 上目的檔剛好被別人開著（編輯器、防毒）會 PermissionError，稍等重試。"""
    for i in range(10):
        try:
            os.replace(tmp, dst)
            return
        except PermissionError:
            if not IS_WIN or i == 9:
                raise
            time.sleep(0.1)


def _write_config(cfg: dict[str, Any]) -> None:
    os.makedirs(os.path.dirname(CONFIG), exist_ok=True)
    tmp = CONFIG + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, sort_keys=True)
    # 原子替換，避免 wall42 剛好讀到寫到一半的檔案
    _replace(tmp, CONFIG)


# ── Windows：行程與狀態檔 ──────────────────────────────────────────
# 注意：Windows 的 os.kill(pid, 0) 會呼叫 TerminateProcess 把對方殺掉，絕對不能拿來探測存活。

def _win_proc_start(pid: int) -> int | None:
    """pid 還活著就回傳它的建立時間（FILETIME，Claude Code 寫在 procStart 的同一個單位），否則 None。"""
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.restype = wintypes.HANDLE
    k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k32.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    k32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    k32.CloseHandle.argtypes = [wintypes.HANDLE]
    if pid <= 0:
        return None
    h = k32.OpenProcess(0x1000, False, pid)          # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    try:
        code = wintypes.DWORD()
        if not k32.GetExitCodeProcess(h, ctypes.byref(code)) or code.value != 259:   # STILL_ACTIVE
            return None
        t = [wintypes.FILETIME() for _ in range(4)]
        if not k32.GetProcessTimes(h, *[ctypes.byref(x) for x in t]):
            return None
        return (t[0].dwHighDateTime << 32) | t[0].dwLowDateTime
    finally:
        k32.CloseHandle(h)


def _win_alive(pid: int, proc_start: Any) -> bool:
    """pid 活著、而且（有記錄建立時間的話）是同一個行程：pid 被別的程式重用不算。"""
    created = _win_proc_start(pid)
    if created is None:
        return False
    try:
        want = int(proc_start)
    except (TypeError, ValueError):
        return True
    return want == 0 or abs(created - want) <= 100_000     # 10 ms


def _win_status() -> dict[str, Any] | None:
    try:
        with open(STATUS, encoding="utf-8-sig") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return None


def _win_tasklist() -> list[int]:
    r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq wall42.exe", "/FO", "CSV", "/NH"],
                       capture_output=True, text=True, creationflags=0x08000000)   # CREATE_NO_WINDOW
    pids = []
    for line in r.stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower().strip('"') == "wall42.exe" and parts[1].isdigit():
            pids.append(int(parts[1]))
    return pids


def _win_pid() -> int | None:
    """狀態檔記錄的那個實例（pid＋建立時間吻合）；沒有狀態檔才退回找 wall42.exe。"""
    st = _win_status()
    if st and st.get("running") and isinstance(st.get("pid"), int) and _win_alive(st["pid"], st.get("procStart")):
        return st["pid"]
    if st and st.get("pid"):
        return None          # 狀態檔在、但那個行程已經結束：不去猜別的 wall42.exe
    pids = _win_tasklist()
    return pids[0] if pids else None


def _running() -> bool:
    if IS_WIN:
        return _win_pid() is not None
    return subprocess.run(["pgrep", "-x", "wall42"],
                          capture_output=True).returncode == 0


def _win_live() -> dict[str, Any]:
    """Windows 的即時狀態：wall42.exe 每 5 秒重算、有變才寫的 status.json。"""
    st = _win_status()
    if not st:
        return {}
    keys = ("state", "occluded", "paused", "suspend", "fps", "cpuPercent", "cpuPercentMachine", "memoryMB",
            "privateMB", "links", "activity", "thinking", "preset", "effect", "drawCount", "sessions",
            "monitors", "lastSync", "syncDryRun", "at", "updatedAt")
    out = {k: st[k] for k in keys if k in st}
    if st.get("running") and not _win_alive(st.get("pid", 0), st.get("procStart")):
        out["state"] = "stopped"
        out["note"] = "狀態檔是舊的：記錄的行程已經不在"
    return out


def _win_fresh_live() -> dict[str, Any]:
    """請 wall42 立刻重寫狀態檔（{"kind":"status"}），再讀。沒在跑就直接讀舊的。"""
    if _win_pid() is None:
        return _win_live()
    path = os.path.join(SIGNAL_DIR, ".signal")
    try:
        # 別的信號還沒被處理（另一個 client 剛送的 think…）：別蓋掉它，等一下；等不到就讀現有的
        for _ in range(5):
            if not os.path.exists(path):
                break
            time.sleep(0.3)
        else:
            return _win_live()
        _write_signal({"kind": "status"})
        for _ in range(8):
            time.sleep(0.3)
            if not os.path.exists(path):
                break
    except OSError:
        pass
    return _win_live()


def _last_log() -> dict[str, Any]:
    """從 log 最後一行取得即時狀態。遮擋中的那行欄位比較少。"""
    if IS_WIN:
        return _win_live()
    try:
        with open(LOG) as f:
            lines = [l for l in f.readlines()[-40:] if "visible" in l or "OCCLUDED" in l]
    except FileNotFoundError:
        return {}
    if not lines:
        return {}
    line = lines[-1]
    out: dict[str, Any] = {"occluded": "OCCLUDED" in line}
    for key, pat in (("fps", r"fps=([\d.]+)"), ("cpuPercent", r"cpu=([\d.]+)%"),
                     ("links", r"links=(\d+)"), ("activity", r"act=([\d.]+)"),
                     ("memoryMB", r"mem=([\d.]+)MB")):
        m = re.search(pat, line)
        if m:
            out[key] = float(m.group(1)) if "." in m.group(1) else int(m.group(1))
    m = re.match(r"\[(\d\d:\d\d:\d\d)\]", line)
    if m:
        out["at"] = m.group(1)
    return out


def _settled_live() -> dict[str, Any]:
    """改完設定、等過每秒檢查之後的即時狀態。Windows 的狀態檔 5 秒才重算一次，所以請它立刻重寫。"""
    return _win_fresh_live() if IS_WIN else _last_log()


@mcp.tool(description=(
    "wall42 目前狀態：是否在執行、被遮擋與否、fps、自身 CPU/記憶體、連線數、"
    "活動度（0=閒置 1=全力運算）、目前的顏色與粒子設定。"))
def wall42_status() -> dict[str, Any]:
    cfg = _read_config()
    m = cfg.get("motion", {})
    act = m.get("activity") or {}
    return {
        "running": _running(),
        "live": _win_fresh_live() if IS_WIN else _last_log(),
        "motion": {
            "effect": m.get("effect"),
            "particleCount": m.get("particleCount"),
            "fps": m.get("fps"),
            "colorA": m.get("colorA"),
            "colorB": m.get("colorB"),
            "link": m.get("link"),
            "pulse": m.get("pulse"),
        },
        "activity": {
            "source": act.get("source", "system"),
            "manualLevel": act.get("manualLevel", 0),
            "smoothing": act.get("smoothing", 0.85),
        },
        "background": cfg.get("background"),
    }


@mcp.tool(description="列出可切換的預設風格與說明。")
def wall42_list_presets() -> dict[str, Any]:
    desc = {
        "neon": "cyan / hot pink，賽博霓虹",
        "deepsea": "青綠到藍，慢速，結構綿密",
        "amber": "暖色琥珀，夜間不刺眼",
        "starfield": "無連線，400 顆小點",
        "starfield2": "星空加強版：冪次尺寸、獨立閃爍",
        "starfield-constellation": "星空＋星座連線（只連亮星）",
        "starfield-web": "星空＋全連線網格（較密）",
        "neural": "AI 運算：密連線＋流動脈衝，跟著系統負載變化",
        "minimal": "純黑底、無連線、少量大光點",
        "snow": "下雪：雪花緩降左右飄，落到底部淡出，兩螢幕連續",
        "sand": "流沙：每台螢幕一道細沙流，底部堆成沙丘緩緩下沉，像沙漏",
        "sessions": "kang 風格＋每個 Claude session 一個帶外環的常駐光點，忙碌時發起更多查詢",
    }
    try:
        found = sorted(f[:-5] for f in os.listdir(PRESETS) if f.endswith(".json"))
    except FileNotFoundError:
        raise ToolError(f"找不到 presets 目錄：{PRESETS}")
    return {"presets": [{"name": n, "description": desc.get(n, "")} for n in found]}


@mcp.tool(description=(
    "切換預設風格。會保留目前的 activity 設定（不會把手動拉高的忙碌狀態洗掉）。"))
def wall42_set_preset(name: str) -> dict[str, Any]:
    src = os.path.join(PRESETS, f"{name}.json")
    if not os.path.exists(src):
        raise ToolError(f"找不到風格 {name}；用 wall42_list_presets 看有哪些")
    with open(src) as f:
        new = json.load(f)
    try:
        old_act = _read_config().get("motion", {}).get("activity")
    except ToolError:
        old_act = None
    if old_act:
        new.setdefault("motion", {})["activity"] = old_act
    _write_config(new)
    time.sleep(1.4)   # 等 wall42 的每秒檢查抓到 mtime 變化
    return {"applied": name, "live": _settled_live()}


@mcp.tool(description=(
    "設定活動度，也就是畫面的忙碌程度。"
    "mode=manual 搭配 level（0..1）手動指定：開始跑長時間運算前拉到 0.8~1.0，"
    "畫面的脈衝會沿連線加速流動、線條變亮，看起來像在運算。"
    "mode=system 交還給系統 CPU 負載自動決定（結束後請設回這個）。mode=off 完全關閉。"))
def wall42_set_activity(
    mode: Literal["system", "manual", "off"],
    level: float | None = None,
    smoothing: float | None = None,
) -> dict[str, Any]:
    if mode == "manual" and level is None:
        raise ToolError("mode=manual 必須同時給 level（0..1）")
    if level is not None and not (0 <= level <= 1):
        raise ToolError(f"level 必須在 0..1 之間，收到 {level}")
    cfg = _read_config()
    act = cfg.setdefault("motion", {}).setdefault("activity", {})
    act["source"] = mode
    if level is not None:
        act["manualLevel"] = float(level)
    if smoothing is not None:
        if not (0 <= smoothing < 1):
            raise ToolError("smoothing 必須在 0..0.99 之間")
        act["smoothing"] = float(smoothing)
    _write_config(cfg)
    time.sleep(1.4)
    return {"activity": act, "live": _settled_live()}


@mcp.tool(description=(
    "改單一設定值。path 用點號指定，例如 motion.particleCount、motion.link.distance、"
    "motion.pulse.speed、background.centerColor。值的型別要與原本相同。"
    "可改的完整清單見 repo 的 README。"))
def wall42_set(path: str, value: Any) -> dict[str, Any]:
    cfg = _read_config()
    parts = path.split(".")
    node = cfg
    for p in parts[:-1]:
        if not isinstance(node.get(p), dict):
            raise ToolError(f"路徑 {path} 不存在（卡在 {p}）")
        node = node[p]
    key = parts[-1]
    if key not in node:
        raise ToolError(f"設定裡沒有 {path}；可用的鍵：{sorted(node.keys())}")
    old = node[key]
    if old is not None and type(old) is not type(value):
        # bool 是 int 的子型別，要特別擋掉
        if not (isinstance(old, (int, float)) and isinstance(value, (int, float))
                and not isinstance(old, bool) and not isinstance(value, bool)):
            raise ToolError(f"{path} 原本是 {type(old).__name__}，不能設成 {type(value).__name__}")
    node[key] = value
    _write_config(cfg)
    time.sleep(1.4)
    return {"path": path, "old": old, "new": value, "live": _settled_live()}


def _write_signal(payload: dict[str, Any]) -> str:
    path = os.path.join(SIGNAL_DIR, ".signal")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    _replace(tmp, path)       # 原子寫入，免得 wall42 讀到半個檔
    return path


def _signal(payload: dict[str, Any]) -> None:
    """事件走檔案信號，wall42 每秒檢查一次；不需要 socket 或 IPC。"""
    if not _running():
        raise ToolError("wall42 沒有在執行（先用 wall42_control start）")
    path = _write_signal(payload)
    for _ in range(4):
        time.sleep(0.6)
        if not os.path.exists(path):
            return
    raise ToolError("信號送出後 wall42 沒有處理，可能卡住了")


@mcp.tool(description=(
    "開始一段『AI 正在運算』的畫面：思考密度拉高、脈衝加速、線條變亮。"
    "在你即將跑長時間任務（大量檔案處理、編譯、搜尋、多輪推理）之前呼叫。"
    "**到期會自動回復**成原本的來源，所以不需要記得關掉；"
    "任務提早結束想立刻收掉的話，用 wall42_set_activity(mode='system')。"))
def wall42_think(seconds: float = 120, level: float = 0.85) -> dict[str, Any]:
    if not (0 <= level <= 1):
        raise ToolError(f"level 必須在 0..1 之間，收到 {level}")
    if not (5 <= seconds <= 3600):
        raise ToolError(f"seconds 必須在 5..3600 之間，收到 {seconds}")
    _signal({"kind": "think", "level": float(level), "seconds": float(seconds)})
    return {"thinking": True, "level": level, "seconds": seconds, "live": _settled_live()}


@mcp.tool(description=(
    "觸發一次『想通了』的視覺脈衝：畫面整體閃一下，"
    "attention 模式還會爆出一個查詢範圍特別大的焦點。"
    "用在你真的解決一個問題、找到答案、或完成一個階段的當下。"
    "效果約 1.3 秒後自然衰減，不需要復原。"))
def wall42_insight(strength: float = 1.0) -> dict[str, Any]:
    if not (0 <= strength <= 1.5):
        raise ToolError(f"strength 必須在 0..1.5 之間，收到 {strength}")
    _signal({"kind": "insight", "strength": float(strength)})
    return {"pulsed": True, "strength": strength}


@mcp.tool(description=(
    "把 wall42 目前的畫面抓一張設成系統桌布。"
    "wall42 是蓋在桌布上的視窗、沒有改系統設定，所以系統設定的預覽跟實際畫面會不一致，"
    "而且開機到 wall42 啟動之間會露出舊桌布——這個工具把那兩個落差補起來。"
    "換過風格或調過顏色之後可以再呼叫一次重拍。"
    "dry_run=True（僅 Windows）：照樣擷取並存圖、記下目前桌布，但不呼叫設定桌布。"))
def wall42_sync_wallpaper(dry_run: bool = False) -> dict[str, Any]:
    if not _running():
        raise ToolError("wall42 沒有在執行，無法擷取畫面（先用 wall42_control start）")
    if IS_WIN:
        return _win_sync_wallpaper(dry_run)
    if dry_run:
        raise ToolError("dry_run 只在 Windows 版支援")
    req = os.path.join(HOME, ".config/wall42/.sync-request")
    os.makedirs(os.path.dirname(req), exist_ok=True)
    open(req, "w").close()
    # 常駐每秒檢查一次，等它處理完
    for _ in range(10):
        time.sleep(1)
        if not os.path.exists(req):
            break
    else:
        raise ToolError("送出要求後逾時，wall42 可能沒在跑或卡住了")
    try:
        with open(LOG) as f:
            tail = [l.strip() for l in f.readlines()[-10:] if "同步桌布" in l]
    except FileNotFoundError:
        tail = []
    return {"synced": any("完成" in l for l in tail), "log": tail}


def _win_sync_wallpaper(dry_run: bool) -> dict[str, Any]:
    """Windows：.sync-request（內容 {"dry":true} 表示只演練）→ wall42.exe 每台螢幕離線渲染目前這一幀、
    存 %LOCALAPPDATA%\\wall42\\wallpaper_a|b_<n>.png、用 IDesktopWallpaper 逐螢幕設定。
    wall42.exe 設了 WALL42_SYNC_DRY=1 的話一律只演練。"""
    before = (_win_status() or {}).get("lastSync") or {}
    req = os.path.join(SIGNAL_DIR, ".sync-request")
    os.makedirs(SIGNAL_DIR, exist_ok=True)
    tmp = req + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(json.dumps({"dry": True}) if dry_run else "")
    _replace(tmp, req)
    for _ in range(10):
        time.sleep(1)
        if not os.path.exists(req):
            break
    else:
        raise ToolError("送出要求後逾時，wall42 可能沒在跑或卡住了")
    # 處理完會強制重寫狀態檔；等 lastSync 換成新的
    last: dict[str, Any] = {}
    for _ in range(20):
        last = (_win_status() or {}).get("lastSync") or {}
        if last and last != before:
            break
        time.sleep(0.25)
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            tail = [l.strip() for l in f.readlines()[-20:] if "wallpaper" in l]
    except FileNotFoundError:
        tail = []
    return {"synced": bool(last) and "error" not in last and last != before,
            "dryRun": bool(last.get("dryRun")), "files": last.get("files", []),
            "applied": last.get("applied", 0), "log": tail[-8:]}


def _scan_claude_sessions() -> list[dict[str, Any]]:
    """跟 wall42 自己的讀法一致：~/.claude/sessions/<pid>.json，pid 活著才算。
    Windows 另外比對 procStart（行程建立時間），pid 被重用的不算。"""
    d = os.path.join(HOME, ".claude/sessions")
    out = []
    try:
        names = os.listdir(d)
    except FileNotFoundError:
        return out
    for n in names:
        if not n.endswith(".json"):
            continue
        try:
            with open(os.path.join(d, n), encoding="utf-8-sig") as f:
                o = json.load(f)
            pid = int(o["pid"])
            if IS_WIN:
                if not _win_alive(pid, o.get("procStart")):
                    continue
            else:
                os.kill(pid, 0)
        except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
            continue
        out.append({"id": o.get("sessionId", f"pid-{pid}"), "name": o.get("name", ""),
                    "busy": o.get("status") == "busy", "startedAt": o.get("startedAt", 0)})
    return sorted(out, key=lambda x: x["startedAt"])


@mcp.tool(description=(
    "Claude session 光點：畫面上每個 session 對應一個帶外環的常駐亮點，忙碌的會持續發光、"
    "在 attention 網路裡更頻繁地發起查詢。"
    "不給 count ＝ 自動模式：wall42 自己讀 ~/.claude/sessions（pid 還活著的、status=busy 算忙碌），"
    "回傳目前偵測到的清單。給 count（0..64）＝ 由外部餵數字，busy 是其中忙碌的個數，"
    "寫到 sessions.json（macOS ~/.config/wall42、Windows %APPDATA%\\wall42），直到再次呼叫不給 count 才回到自動。"
    "enable=True 會在目前設定打開 motion.sessions.enabled（任何風格都能疊加光點）；"
    "也可以直接切 sessions 風格。"))
def wall42_sessions(count: int | None = None, busy: int = 0,
                    enable: bool = False) -> dict[str, Any]:
    feed = os.path.join(SIGNAL_DIR, "sessions.json")
    if count is None:
        if os.path.exists(feed):
            os.remove(feed)
        mode = "auto"
    else:
        if not (0 <= count <= 64):
            raise ToolError(f"count 必須在 0..64 之間，收到 {count}")
        if not (0 <= busy <= count):
            raise ToolError(f"busy 必須在 0..count 之間，收到 {busy}")
        os.makedirs(SIGNAL_DIR, exist_ok=True)
        tmp = feed + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"count": int(count), "busy": int(busy)}, f)
        _replace(tmp, feed)
        mode = "file"
    cfg = _read_config()
    ses = cfg.setdefault("motion", {}).get("sessions") or {}
    if enable and not ses.get("enabled"):
        ses["enabled"] = True
        cfg["motion"]["sessions"] = ses
        _write_config(cfg)
    time.sleep(2.2)     # wall42 每 2 秒更新一次 session 清單
    out: dict[str, Any] = {"mode": mode, "enabledInConfig": bool(ses.get("enabled"))}
    if mode == "auto":
        found = _scan_claude_sessions()
        out["detected"] = [{"name": s["name"], "busy": s["busy"]} for s in found]
    else:
        out["fed"] = {"count": count, "busy": busy}
    if not out["enabledInConfig"]:
        out["note"] = "目前設定沒開 motion.sessions.enabled，畫面上不會出現光點；帶 enable=True 或切 sessions 風格"
    if IS_WIN:
        # wall42.exe 只在畫面露出來（有在畫）時讀 session；被視窗蓋住時這裡是舊值，露出來 1 秒內更新
        live = _win_fresh_live()
        if "sessions" in live:
            out["onScreen"] = live["sessions"]
    return out


def _win_exe() -> str | None:
    """WALL42_EXE 優先，其次安裝位置，最後 repo 裡的 Release 編譯產物。"""
    cands = [os.environ.get("WALL42_EXE") or "",
             os.path.join(_LOCAL, "wall42", "wall42.exe"),
             os.path.join(_LOCAL, "Programs", "wall42", "wall42.exe"),
             os.path.join(REPO, "windows", "Wall42.Win", "bin", "Release", "net8.0-windows", "wall42.exe")]
    for c in cands:
        if c and os.path.isfile(c):
            return c
    return None


def _win_wait_exit(pid: int, proc_start: Any, seconds: float) -> bool:
    end = time.time() + seconds
    while time.time() < end:
        if not _win_alive(pid, proc_start):
            return True
        time.sleep(0.2)
    return not _win_alive(pid, proc_start)


def _win_control(action: str) -> dict[str, Any]:
    no_window = 0x08000000                                   # CREATE_NO_WINDOW
    if action in ("stop", "restart"):
        pid = _win_pid()
        if pid is not None:
            ps = (_win_status() or {}).get("procStart")
            # 不帶 /f：送 WM_CLOSE，wall42 自己收尾（寫狀態檔、請 Explorer 重畫桌面）
            subprocess.run(["taskkill", "/pid", str(pid)], capture_output=True, creationflags=no_window)
            if not _win_wait_exit(pid, ps, 6):
                subprocess.run(["taskkill", "/f", "/pid", str(pid)], capture_output=True, creationflags=no_window)
                _win_wait_exit(pid, ps, 3)
    if action in ("start", "restart"):
        if _win_pid() is None:
            exe = _win_exe()
            if exe is None:
                raise ToolError("找不到 wall42.exe（設定 WALL42_EXE，或先安裝）")
            flags = 0x00000008 | 0x00000200 | no_window          # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
            subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=flags, close_fds=True,
                             stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for _ in range(20):
                time.sleep(0.25)
                if _win_pid() is not None:
                    break
    time.sleep(1.0)
    return {"action": action, "running": _running(), "pid": _win_pid(), "live": _win_live()}


@mcp.tool(description="啟動／停止／重啟常駐。停止後桌面會回到系統原本的桌布。")
def wall42_control(action: Literal["start", "stop", "restart"]) -> dict[str, Any]:
    if IS_WIN:
        return _win_control(action)
    uid = os.getuid()
    target = f"gui/{uid}/{LABEL}"
    if action in ("stop", "restart"):
        subprocess.run(["launchctl", "bootout", target], capture_output=True)
    if action in ("start", "restart"):
        plist = os.path.join(HOME, "Library/LaunchAgents", f"{LABEL}.plist")
        if not os.path.exists(plist):
            raise ToolError(f"找不到 {plist}，wall42 還沒安裝（跑 ./install.sh）")
        r = subprocess.run(["launchctl", "bootstrap", f"gui/{uid}", plist],
                           capture_output=True, text=True)
        if r.returncode != 0 and "already bootstrapped" not in r.stderr:
            raise ToolError(f"啟動失敗：{r.stderr.strip()}")
    time.sleep(1.5)
    return {"action": action, "running": _running(), "live": _last_log()}


if __name__ == "__main__":
    mcp.run()
