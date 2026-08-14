#!/usr/bin/env python3
"""IEEE の MA-L 登録簿から、MAC 先頭 3 バイト → ベンダー名の最小テーブルを作る。

生成物 (src/PastelNet.App/Resources/oui.tsv.gz) はリポジトリにコミットして
埋め込みリソースとして配布する。CI では IEEE を叩かない — ネットワークに
依存させるとビルドの再現性が壊れるため。

使い方:
    python3 tools/oui/build_oui.py

登録簿を更新したくなったときに手で実行する。
"""

from __future__ import annotations

import csv
import gzip
import io
import os
import re
import sys
import urllib.request

URL = "https://standards-oui.ieee.org/oui/oui.csv"
OUT = "src/PastelNet.App/Resources/oui.tsv.gz"

# 会社名の末尾に付く法人格。表示には要らないので落としてサイズを稼ぐ。
SUFFIXES = re.compile(
    r"[,\s]+(inc|inc\.|incorporated|corp|corp\.|corporation|co|co\.|ltd|ltd\.|limited"
    r"|llc|l\.l\.c\.|gmbh|ag|s\.a\.|s\.a|sa|b\.v\.|bv|n\.v\.|nv|oy|ab|as|a/s|plc"
    r"|pty|pte|sdn\.?\s*bhd|s\.r\.l\.|srl|s\.p\.a\.|spa|kk|k\.k\.|co\.,?\s*ltd\.?)\.?$",
    re.IGNORECASE,
)


def tidy(name: str) -> str:
    """表示用にベンダー名を短くする。判別できなくなるほどは削らない。"""
    name = " ".join(name.split())

    # 末尾の法人格は繰り返し落とす（"Foo Technologies Co., Ltd." など）
    for _ in range(3):
        shortened = SUFFIXES.sub("", name).rstrip(" ,.")
        if shortened == name or not shortened:
            break
        name = shortened

    return name


def main() -> int:
    print(f"取得中: {URL}")

    # User-Agent を付けないと 418 で弾かれる
    request = urllib.request.Request(
        URL,
        headers={"User-Agent": "PastelNet-oui-builder/1.0 (+https://github.com/izenmi/pastelnet)"},
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        raw = response.read()
    print(f"  {len(raw):,} バイト")

    text = raw.decode("utf-8", errors="replace")
    reader = csv.DictReader(io.StringIO(text))

    entries: dict[str, str] = {}
    for row in reader:
        assignment = (row.get("Assignment") or "").strip().upper()
        organization = (row.get("Organization Name") or "").strip()

        if len(assignment) != 6 or not organization:
            continue
        if organization.lower() in ("private", "ieee registration authority"):
            continue

        entries[assignment] = tidy(organization)

    print(f"  {len(entries):,} 件")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)

    # 先頭 3 バイトの昇順で並べる（差分を安定させ、圧縮も効きやすくする）
    body = "".join(f"{oui}\t{name}\n" for oui, name in sorted(entries.items()))

    # mtime を固定して、内容が同じなら毎回同じファイルになるようにする
    with open(OUT, "wb") as file:
        with gzip.GzipFile(fileobj=file, mode="wb", compresslevel=9, mtime=0) as gz:
            gz.write(body.encode("utf-8"))

    print(f"書き出し: {OUT}  {os.path.getsize(OUT):,} バイト（展開後 {len(body.encode('utf-8')):,}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
