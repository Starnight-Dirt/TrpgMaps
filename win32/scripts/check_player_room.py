"""玩家页真浏览器检查（无头 Edge）。

跑法：python scripts/check_player_room.py
它会：起一个 TrpgMaps 实例（--windowed --port 20020）→ 读 room.json 拿房间号 →
设一个房间密码 → 用无头 Edge 打开玩家页的两种地址（带/不带 ?passwd=）各 dump 一次 DOM
并各截一张图 → 再打开 /join 页（房间号手填的那种）dump 一次 → 关掉实例。

断言（打印出来，非 0 退出码 = 有失败）：
  · 页面能在 /<房间号>/player 下打开，且没有 JS 报错（debug 面板第一行是 player.js 修订号）
  · 不带密码时：#room-pass-input 是 display:block（要多问一次密码）
  · 带对密码时：#room-pass-input 是 display:none（只问玩家名）
  · debug 面板里的"房间 URL / 服务器"两个号一致
  · /join 页：房间号框显示、密码框也一直显示（三个框），且 debug 面板认得 /join
  · /api/room?room=&passwd= 的三种回答：房间不存在 / 密码错误 / 都对（且不回密码本身）

⚠️ 无头 Edge 只有 `--headless=old` 这一支肯老实干活（new 那一支在本机静默不出图），
   这是 2026-10-12 实测出来的（之前一度以为"无头 Edge 在本机完全失效"）。
"""

import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "build_logs", "player_room")
PORT = 20020
PASSWORD = "pw123"

failures = []


def check(name, ok, extra=""):
    print(("  [PASS] " if ok else "  [FAIL] ") + name + (" " + extra if extra else ""))
    if not ok:
        failures.append(name)


def http(url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    req = urllib.request.Request(url, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=5) as res:
        return res.read().decode("utf-8")


def edge(args, timeout=60):
    """跑一次无头 Edge，返回 stdout（--dump-dom 用）。

    三个实测出来的坑（2026-10-12/13）：
      · 只有 `--headless=old` 这一支老实干活（new 那一支在本机不出 DOM / 不出图）；
      · `--user-data-dir` **每次都要新的** —— 复用会被上一次残留的 Edge 进程锁住，然后挂死；
      · `--screenshot` 与 `--virtual-time-budget` **不能一起用**（页面上的 SSE 长连接
        让"加载完成"永远等不来）。要 DOM 就带 virtual-time-budget，要截图就不带。
    """
    profile = os.path.join(OUT, "_edge_%d" % int(time.time() * 1000))
    cmd = [EDGE, "--headless=old", "--disable-gpu", "--no-first-run",
           "--user-data-dir=" + profile] + args
    try:
        proc = subprocess.run(cmd, capture_output=True, timeout=timeout)
        return proc.stdout.decode("utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        print("     (edge timed out: " + " ".join(args) + ")")
        return ""
    finally:
        shutil.rmtree(profile, ignore_errors=True)


def main():
    os.makedirs(OUT, exist_ok=True)

    exe = os.path.join(ROOT, "bin", "Release", "TrpgMaps.exe")
    if not os.path.exists(exe):
        print("TrpgMaps.exe not found: " + exe)
        return 1

    log_path = os.path.join(ROOT, "bin", "Release", "app.log")
    if os.path.exists(log_path):
        os.remove(log_path)

    proc = subprocess.Popen([exe, "--windowed", "--no-prompts", "--port", str(PORT)],
                            cwd=os.path.join(ROOT, "bin", "Release"),
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        base = "http://127.0.0.1:%d" % PORT

        ready = False
        for _ in range(40):
            time.sleep(0.5)
            try:
                http(base + "/api/room")
                ready = True
                break
            except Exception:
                pass
        check("server started on port %d" % PORT, ready)
        if not ready:
            return 1

        room = json.load(open(os.path.join(ROOT, "bin", "Release", "room.json"),
                              encoding="utf-8-sig"))["room"]
        print("  room number: " + room)
        check("room number is 8 digits", len(room) == 8 and room.isdigit(), room)

        # 设一个房间密码（顺便验证它会写进 room.json）
        res = json.loads(http(base + "/api/room/password", {"password": PASSWORD}))
        check("password set through the api", res.get("success") is True, str(res))

        saved = json.load(open(os.path.join(ROOT, "bin", "Release", "room.json"),
                               encoding="utf-8-sig"))
        check("password persisted to room.json", saved.get("password") == PASSWORD)

        # ---- ① 不带密码：登录面板要**多问一个密码** ----
        # ⚠️ 不要带 --virtual-time-budget：页面上的 SSE 长连接让"加载完成"永远等不到，
        # 那个参数会让 Edge 挂死（实测 60s 超时）。裸 --dump-dom 反而是把好手。
        url_no_pw = "%s/%s/player?debug=1" % (base, room)
        dom = edge(["--dump-dom", url_no_pw])
        open(os.path.join(OUT, "dom-no-passwd.html"), "w", encoding="utf-8").write(dom)

        check("player page loads (player.js rev printed)",
              "player.js r27" in dom, _grab(dom, "player.js r"))
        check("room parsed from the url path", "房间 URL " + room in dom, _grab(dom, "房间 URL"))
        check("server room matches the url room", "服务器 " + room in dom, _grab(dom, "服务器"))
        check("password field is SHOWN without ?passwd=",
              'id="room-pass-input"' in dom and "display: block" in _tag(dom, "room-pass-input"),
              _tag(dom, "room-pass-input"))
        check("player-fab element present", 'id="player-fab"' in dom)

        # ---- ② 带对密码：只问玩家名 ----
        url_with_pw = "%s/%s/player?passwd=%s&debug=1" % (base, room, PASSWORD)
        dom2 = edge(["--dump-dom", url_with_pw])
        open(os.path.join(OUT, "dom-with-passwd.html"), "w", encoding="utf-8").write(dom2)

        check("password field is HIDDEN with the right ?passwd=",
              "display: none" in _tag(dom2, "room-pass-input"),
              _tag(dom2, "room-pass-input"))
        check("debug panel reports the password as carried", "已带密码" in dom2, _grab(dom2, "密码"))

        # ---- ④ /join 页：房间号 + 玩家名 + 密码三个框，接口把两种错分开回答 ----
        # 页面上的提示就是照这三个分支来的：房间号不对 → "房间不存在"；
        # 房间在但密码不对 → "房间密码错误"；都对 → 才去认玩家名。
        bad_room = json.loads(http(base + "/api/room?room=00000000&passwd=" + PASSWORD))
        check("join check: unknown room -> exists=false", bad_room.get("exists") is False, str(bad_room))

        bad_pw = json.loads(http(base + "/api/room?room=%s&passwd=wrong-one" % room))
        check("join check: right room + wrong password -> passwordOk=false",
              bad_pw.get("exists") is True and bad_pw.get("passwordOk") is False, str(bad_pw))

        good = json.loads(http(base + "/api/room?room=%s&passwd=%s" % (room, PASSWORD)))
        check("join check: right room + right password",
              good.get("exists") is True and good.get("passwordOk") is True, str(good))
        check("join check never leaks the password", PASSWORD not in json.dumps(good))

        url_join = base + "/join?debug=1"
        dom3 = edge(["--dump-dom", url_join])
        open(os.path.join(OUT, "dom-join.html"), "w", encoding="utf-8").write(dom3)

        check("join page shows the room-number box",
              "display: block" in _tag(dom3, "room-number-input"), _tag(dom3, "room-number-input"))
        check("join page keeps the password box visible (three boxes)",
              "display: block" in _tag(dom3, "room-pass-input"), _tag(dom3, "room-pass-input"))
        check("join page knows it is /join", "/join(手填)" in dom3, _grab(dom3, "房间 URL"))
        check("join page does not auto-login", 'id="player-name-input"' in dom3)

        # ---- ③ 截图（目视：登录面板 / 密码框 / 悬浮按钮的位置与样式） ----
        # 不能带 virtual-time-budget（会和 --screenshot 一起挂，见 edge() 的说明）。
        shot1 = os.path.join(OUT, "01-login-need-passwd.png")
        if os.path.exists(shot1):
            os.remove(shot1)
        edge(["--window-size=430,860", "--screenshot=" + shot1, url_no_pw])
        size1 = os.path.getsize(shot1) if os.path.exists(shot1) else 0
        check("screenshot written (needs-password page)", size1 > 5000, "%d bytes" % size1)

        shot2 = os.path.join(OUT, "02-login-with-passwd.png")
        if os.path.exists(shot2):
            os.remove(shot2)
        edge(["--window-size=430,860", "--screenshot=" + shot2, url_with_pw])
        size2 = os.path.getsize(shot2) if os.path.exists(shot2) else 0
        check("screenshot written (with-password page)", size2 > 5000, "%d bytes" % size2)

        shot3 = os.path.join(OUT, "03-join-three-boxes.png")
        if os.path.exists(shot3):
            os.remove(shot3)
        edge(["--window-size=430,860", "--screenshot=" + shot3, url_join])
        size3 = os.path.getsize(shot3) if os.path.exists(shot3) else 0
        check("screenshot written (/join page)", size3 > 5000, "%d bytes" % size3)

        return 0 if not failures else 1
    finally:
        try:
            proc.terminate()
        except Exception:
            pass


def _tag(dom, element_id):
    """取某个元素整个开标签（用来读它的 style 属性）。"""
    marker = 'id="%s"' % element_id
    index = dom.find(marker)
    if index < 0:
        return ""
    start = dom.rfind("<", 0, index)
    end = dom.find(">", index)
    return dom[start:end + 1]


def _grab(dom, prefix):
    index = dom.find(prefix)
    return dom[index:index + 40] if index >= 0 else "(not found)"


if __name__ == "__main__":
    print("=== player page: room / password (headless Edge) ===")
    code = main()
    print("")
    print("ALL PLAYER ROOM CHECKS PASSED" if code == 0 else "%d CHECK(S) FAILED" % len(failures))
    sys.exit(code)
