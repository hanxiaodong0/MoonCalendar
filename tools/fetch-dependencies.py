"""Refresh vendored dependencies explicitly; normal builds do not use the network."""
import concurrent.futures
from datetime import date
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
JOBS = [
    (f"https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json", f"data/{year}.json")
    for year in (2025, 2026, 2027)
] + [
    ("https://api.nuget.org/v3-flatcontainer/lunar-csharp/1.6.9/lunar-csharp.1.6.9.nupkg", "lib/lunar-csharp.1.6.9.nupkg"),
    ("https://raw.githubusercontent.com/6tail/lunar-csharp/v1.6.9/LICENSE", "lib/lunar-csharp.LICENSE"),
    ("https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/LICENSE", "lib/holiday-cn.LICENSE"),
]


def fetch(job):
    url, relative = job
    request = urllib.request.Request(url, headers={"User-Agent": "ShiguangCalendar-build/1.0"})
    data = urllib.request.urlopen(request, timeout=30).read()
    if relative.startswith("data/"):
        parsed = json.loads(data)
        assert parsed["year"] == int(Path(relative).stem)
        assert parsed["papers"] and parsed["days"], "No announced holiday schedule"
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return {"path": relative, "url": url, "sha256": hashlib.sha256(data).hexdigest()}


if __name__ == "__main__":
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        records = list(pool.map(fetch, JOBS))
    with zipfile.ZipFile(ROOT / "lib/lunar-csharp.1.6.9.nupkg") as package:
        for name in ("lunar.dll", "lunar.xml"):
            data = package.read("lib/netstandard2.0/" + name)
            (ROOT / "lib" / name).write_bytes(data)
            records.append({"path": "lib/" + name, "source": "lunar-csharp.1.6.9.nupkg", "sha256": hashlib.sha256(data).hexdigest()})
    (ROOT / "dependencies.json").write_text(json.dumps({"retrieved": date.today().isoformat(), "files": records}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Dependencies downloaded and hashes recorded.")
