#!/usr/bin/env python3
"""Markdown で書いた資料を .docx に焼く（手動実行）。

    python3 tools/docs/build_docx.py

docx は zip に XML を数枚入れただけのもので、必要なのは 4 パーツしかない。
xlsx を自前で書いている（Core/Reporting/XlsxWriter.cs）のと同じ判断で、
**ライブラリは足さない**。この開発環境には pip も無いので、入れられもしない。

対応する書式（資料 2 本に要るものだけ）:

    # 見出し      → 表題
    ## 見出し     → 大見出し / ### → 中見出し / #### → 小見出し
    - 箇条書き    → 「・」（2 段目は全角スペースで下げて「－」）
    1. 番号       → 数字はそのまま文字として置く
    > 注記        → 字下げした淡色の段落
    | 表 | 組み | → 罫線つきの表（1 行目が見出し。2 行目の |---| は読み飛ばす）
    ---           → 改ページ
    **太字** `等幅`

Word は要素の順番に厳しく、間違えると「読めない内容です」と言ってファイルごと
開かない（xlsx で踏んだのと同じ）。pPr / rPr の中の並びを勝手に入れ替えないこと。
"""

import html
import re
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# 焼くもの。増やすときはここに 1 行足す
DOCUMENTS = [
    ("docs/紹介資料.md", "docs/NetworkToys-紹介資料.docx"),
    ("docs/設計資料.md", "docs/NetworkToys-設計資料.docx"),
]

# 本文の書体。日本語は eastAsia 側を見るので、必ず両方に書く
ASCII_FONT = "Segoe UI"
JP_FONT = "Yu Gothic"
CODE_ASCII = "Consolas"
CODE_JP = "MS Gothic"

W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'

CONTENT_TYPES = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" \
ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
<Override PartName="/word/styles.xml" \
ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>"""

RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" \
Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" \
Target="word/document.xml"/>
</Relationships>"""

DOCUMENT_RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" \
Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" \
Target="styles.xml"/>
</Relationships>"""


def fonts(ascii_font=ASCII_FONT, jp_font=JP_FONT):
    return f'<w:rFonts w:ascii="{ascii_font}" w:hAnsi="{ascii_font}" w:eastAsia="{jp_font}" w:cs="{ascii_font}"/>'


def heading_style(style_id, name, size_half_points, color, outline, space_before):
    """見出し 1 つぶんの定義。size は half-point（22 = 11pt）。

    pPr の中は spacing → outlineLvl の順。**逆に書くと Word はファイルごと開かない**
    （xlsx でシートの要素順を間違えたときと同じ）。
    """
    return (
        f'<w:style w:type="paragraph" w:styleId="{style_id}">'
        f'<w:name w:val="{name}"/><w:basedOn w:val="Normal"/>'
        f'<w:pPr><w:keepNext/><w:spacing w:before="{space_before}" w:after="120"/>'
        f'<w:outlineLvl w:val="{outline}"/></w:pPr>'
        f'<w:rPr>{fonts()}<w:b/><w:color w:val="{color}"/>'
        f'<w:sz w:val="{size_half_points}"/><w:szCs w:val="{size_half_points}"/></w:rPr>'
        f"</w:style>"
    )


STYLES = f"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles {W}>
<w:docDefaults><w:rPrDefault><w:rPr>{fonts()}<w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:rPrDefault>
<w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="288" w:lineRule="auto"/></w:pPr></w:pPrDefault>
</w:docDefaults>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
{heading_style("Title", "Title", 40, "1F3864", 0, 0)}
{heading_style("Heading1", "heading 1", 30, "1F3864", 0, 360)}
{heading_style("Heading2", "heading 2", 25, "2E5496", 1, 280)}
{heading_style("Heading3", "heading 3", 22, "44546A", 2, 240)}
<w:style w:type="character" w:styleId="CodeChar"><w:name w:val="Code Char"/>
<w:rPr>{fonts(CODE_ASCII, CODE_JP)}<w:color w:val="9C2B2B"/></w:rPr></w:style>
</w:styles>"""

BORDER = (
    "<w:tblBorders>"
    + "".join(
        f'<w:{side} w:val="single" w:sz="4" w:space="0" w:color="B4C6E7"/>'
        for side in ("top", "left", "bottom", "right", "insideH", "insideV")
    )
    + "</w:tblBorders>"
)


def escape(text):
    return html.escape(text, quote=False)


def run_properties(code=False, bold=False, italic=False, color=None):
    """run の書式。**rStyle → rFonts → b → i → color の順**は動かせない。

    「太字の中の等幅」のように書式が重なることがあるので、
    つなぎ合わせるのではなく必ずこの 1 か所で組み立てる
    （繋げると rPr が 2 つ入った run ができて、Word が開かなくなる）。
    """
    parts = []

    if code:
        parts.append('<w:rStyle w:val="CodeChar"/>')
        parts.append(fonts(CODE_ASCII, CODE_JP))
    if bold:
        parts.append("<w:b/>")
    if italic:
        parts.append("<w:i/>")
    if color:
        parts.append(f'<w:color w:val="{color}"/>')

    return f'<w:rPr>{"".join(parts)}</w:rPr>' if parts else ""


def runs(text, italic=False, color=None):
    """**太字** と `等幅` だけを解釈して、run の並びにする。"""
    out = []

    for part in re.split(r"(\*\*[^*]+\*\*|`[^`]+`)", text):
        if not part:
            continue

        bold = code = False

        if part.startswith("**") and part.endswith("**"):
            part, bold = part[2:-2], True
        elif part.startswith("`") and part.endswith("`"):
            part, code = part[1:-1], True

        # 前後の空白を落とさせない
        out.append(
            f"<w:r>{run_properties(code, bold, italic, color)}"
            f'<w:t xml:space="preserve">{escape(part)}</w:t></w:r>'
        )

    return "".join(out)


def paragraph(text, style=None, indent=0, before=None, color=None, italic=False):
    properties = ""
    if style:
        properties += f'<w:pStyle w:val="{style}"/>'
    if before is not None:
        properties += f'<w:spacing w:before="{before}"/>'
    if indent:
        properties += f'<w:ind w:left="{indent}"/>'

    # 段落の既定書式（pPr の中の rPr）は pPr の最後に置く決まり
    properties += run_properties(italic=italic, color=color)

    return (
        f'<w:p>{"<w:pPr>" + properties + "</w:pPr>" if properties else ""}'
        f"{runs(text, italic, color)}</w:p>"
    )


def page_break():
    return '<w:p><w:r><w:br w:type="page"/></w:r></w:p>'


def cell(text, width, header):
    shade = '<w:shd w:val="clear" w:color="auto" w:fill="D9E2F3"/>' if header else ""
    inner = f"**{text}**" if header and text else text

    return (
        f'<w:tc><w:tcPr><w:tcW w:w="{width}" w:type="dxa"/>{shade}'
        f'<w:vAlign w:val="center"/></w:tcPr>{paragraph(inner)}</w:tc>'
    )


def table(rows):
    """1 行目を見出しにした表。幅はページ幅（9070 twips）を列数で割る。"""
    columns = max(len(r) for r in rows)
    width = 9070 // columns

    xml = [
        f'<w:tbl><w:tblPr><w:tblW w:w="9070" w:type="dxa"/>{BORDER}'
        f'<w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid>'
        + f'<w:gridCol w:w="{width}"/>' * columns
        + "</w:tblGrid>"
    ]

    for index, row in enumerate(rows):
        cells = list(row) + [""] * (columns - len(row))
        header = index == 0

        xml.append(
            "<w:tr>"
            + ('<w:trPr><w:tblHeader/></w:trPr>' if header else "")
            + "".join(cell(c, width, header) for c in cells)
            + "</w:tr>"
        )

    # 表の直後には段落を置く（表で本文が終わると Word が嫌がる）
    return "".join(xml) + "</w:tbl>" + '<w:p><w:pPr><w:spacing w:after="0"/></w:pPr></w:p>'


def split_row(line):
    return [c.strip() for c in line.strip().strip("|").split("|")]


def convert(markdown):
    """Markdown の行を、本文の XML に変換する。"""
    body = []
    lines = markdown.replace("\r\n", "\n").split("\n")
    at = 0

    while at < len(lines):
        line = lines[at].rstrip()
        stripped = line.strip()

        if not stripped:
            at += 1
            continue

        # 表: 連続する | 行をまとめて 1 つにする
        if stripped.startswith("|"):
            rows = []
            while at < len(lines) and lines[at].strip().startswith("|"):
                row = split_row(lines[at])
                # |---|---| の区切り行は表に入れない
                if not all(set(c) <= set("-: ") and c for c in row):
                    rows.append(row)
                at += 1

            if rows:
                body.append(table(rows))
            continue

        at += 1

        if stripped == "---":
            body.append(page_break())
        elif stripped.startswith("#### "):
            body.append(paragraph(stripped[5:], "Heading3"))
        elif stripped.startswith("### "):
            body.append(paragraph(stripped[4:], "Heading2"))
        elif stripped.startswith("## "):
            body.append(paragraph(stripped[3:], "Heading1"))
        elif stripped.startswith("# "):
            body.append(paragraph(stripped[2:], "Title"))
        elif stripped.startswith("> "):
            body.append(paragraph(stripped[2:], indent=400, color="5A5A5A", italic=True))
        elif line.startswith("  - ") or line.startswith("    - "):
            body.append(paragraph("－ " + stripped[2:], indent=720))
        elif stripped.startswith("- "):
            body.append(paragraph("・" + stripped[2:], indent=360))
        elif re.match(r"^\d+\. ", stripped):
            body.append(paragraph(stripped, indent=360))
        else:
            body.append(paragraph(stripped))

    return "".join(body)


def document(markdown):
    section = (
        '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/>'
        '<w:pgMar w:top="1418" w:right="1418" w:bottom="1418" w:left="1418" '
        'w:header="851" w:footer="992" w:gutter="0"/></w:sectPr>'
    )

    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        f"<w:document {W}><w:body>{convert(markdown)}{section}</w:body></w:document>"
    )


def build(source, destination):
    markdown = (ROOT / source).read_text(encoding="utf-8")
    out = ROOT / destination
    out.parent.mkdir(parents=True, exist_ok=True)

    # 中身が同じなら毎回同じバイト列になるよう、日時は固定して書く
    stamp = (2026, 1, 1, 0, 0, 0)

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as book:
        for name, text in (
            ("[Content_Types].xml", CONTENT_TYPES),
            ("_rels/.rels", RELS),
            ("word/_rels/document.xml.rels", DOCUMENT_RELS),
            ("word/styles.xml", STYLES),
            ("word/document.xml", document(markdown)),
        ):
            info = zipfile.ZipInfo(name, date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            book.writestr(info, text)

    print(f"{destination}  ({out.stat().st_size:,} bytes)")


def check(markdown):
    """焼く前の自己検査。**順番の誤りは Word がファイルごと開かない形で出る**ので、
    ここで捕まえられるものは捕まえておく（実物を Word で開けない環境のため）。"""
    import xml.etree.ElementTree as ElementTree

    xml = document(markdown)
    root = ElementTree.fromstring(xml)  # 整形式でなければここで落ちる
    ElementTree.fromstring(STYLES)

    namespace = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"

    # pPr / rPr の中で守るべき並び（この道具が出す要素だけ）
    order = {
        "pPr": ["pStyle", "keepNext", "spacing", "ind", "outlineLvl", "rPr"],
        "rPr": ["rStyle", "rFonts", "b", "i", "color", "sz", "szCs"],
    }

    for name, expected in order.items():
        for node in root.iter(namespace + name):
            actual = [child.tag.removeprefix(namespace) for child in node]
            unknown = [tag for tag in actual if tag not in expected]
            assert not unknown, f"{name} に順番を決めていない要素がある: {unknown}"

            rank = [expected.index(tag) for tag in actual]
            assert rank == sorted(rank), f"{name} の並びが規定と違う: {actual}"

    for run in root.iter(namespace + "r"):
        found = len(run.findall(namespace + "rPr"))
        assert found <= 1, "1 つの run に rPr が 2 つある（書式が重なったときの組み立て漏れ）"

    body = list(root[0])
    assert body[-1].tag == namespace + "sectPr", "本文の最後が sectPr でない"

    for index, node in enumerate(body[:-1]):
        if node.tag == namespace + "tbl":
            assert body[index + 1].tag == namespace + "p", "表の直後に段落が無い（Word が嫌がる）"

    return len(body)


if __name__ == "__main__":
    for source, destination in DOCUMENTS:
        blocks = check((ROOT / source).read_text(encoding="utf-8"))
        build(source, destination)
        print(f"  検査 OK（本文 {blocks} 個）")
