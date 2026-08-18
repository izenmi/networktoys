#!/bin/sh
# 資料に載せる図を SVG から PNG に焼く（手動実行）。
#
#     sh tools/docs/build_figures.sh
#
# docx は PNG しか置けない（SVG は Word が読まない版がある）ので、
# 図の原本は SVG、載せるのは PNG。焼いたものもリポジトリに置く
# （build_docx.py が読むため。docx を焼くときに rsvg-convert を要求しない）。
set -e

cd "$(dirname "$0")/../.."

for svg in docs/figures/*.svg; do
    png="${svg%.svg}.png"
    rsvg-convert -w 1680 "$svg" -o "$png"
    echo "$png"
done
