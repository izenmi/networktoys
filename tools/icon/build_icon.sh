#!/usr/bin/env bash
# AppIcon.svg から Windows 用の .ico を焼く。
#
# 手で実行する。アイコンは滅多に変わらないので CI には入れていない
# （入れると ImageMagick を毎回入れることになり、ビルドが遅くなる）。
#
#   tools/icon/build_icon.sh
#
# 必要なもの: ImageMagick の convert
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

svg="$root/src/NetworkToys.App/Resources/AppIcon.svg"
small="$root/src/NetworkToys.App/Resources/AppIcon.Small.svg"
ico="$root/src/NetworkToys.App/Resources/AppIcon.ico"

for f in "$svg" "$small"; do
    [ -f "$f" ] || { echo "元の SVG が見つかりません: $f" >&2; exit 1; }
done
command -v convert >/dev/null || { echo "ImageMagick の convert が要ります" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Windows がエクスプローラ・タスクバー・Alt+Tab で使う一式。
# 16 と 32 が実際に一番目に入るので、そこを外さないこと。
sizes=(16 20 24 32 40 48 64 128 256)

# 24 以下は簡略版から焼く。通常版をそのまま縮めると接点 8 本が 1px 未満になって
# 全体が濁るため、その大きさ用に描き直した絵を使う。
small_max=24

pngs=()
for size in "${sizes[@]}"; do
    out="$work/icon-$size.png"

    if [ "$size" -le "$small_max" ]; then
        source="$small"
    else
        source="$svg"
    fi

    # 元が小さいと縮小時にぼやけるので、一度大きく描いてから落とす。
    # -depth 8 を付けないと 16bit で焼かれて、ico が無駄に太る。
    convert -background none -density 1200 "$source" \
            -resize "${size}x${size}" \
            -depth 8 \
            "PNG32:$out"

    pngs+=("$out")
done

convert "${pngs[@]}" "$ico"

echo "書き出しました: $ico"
convert "$ico" -format '  %wx%h %[channels] %[bit-depth]bit\n' info: 2>/dev/null || true
