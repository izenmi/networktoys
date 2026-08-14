# PastelNet

ぜんぶ同時に測る、Windows 向けネットワーク診断ツール。

EXPing のように宛先リストへ ping を打てますが、**宛先ごとに独立した周期で並列に測定する**ので、応答しない宛先が他の宛先を待たせません。ping のほかに TCP ping・DNS・traceroute・IP スキャン・無線 LAN の状態を 1 つのアプリにまとめ、測定結果はそのまま現場の証跡として書き出せます。

見た目はパステルカラーで、長時間眺めても疲れないことを狙っています。

## 状態

Phase 0（骨組みと CI の構築）。まだ実際の測定はできません。開発中の進み方は [DESIGN.md](DESIGN.md) を参照してください。

## 動作環境

- Windows 10 / 11 (x64)
- **管理者権限は不要**です。ICMP・TCP・DNS・traceroute・ARP テーブルの読み取り・無線 LAN 情報のいずれも、通常のユーザー権限で動作します。

## 入手

[Actions](../../actions) の最新ビルドから成果物をダウンロードしてください。

| 成果物 | サイズ | 中身 |
|---|---|---|
| `PastelNet-win-x64` | 約 125MB | 単一 exe。.NET のインストール不要。こちらを推奨 |
| `PastelNet-win-x64-runtime-required` | 約 0.2MB | 軽量版。別途 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) が必要 |

WPF はトリミングできないため、.NET を同梱すると 125MB になります。単一ファイルの圧縮でサイズを半分にはできますが、メモリ消費が倍（約 183MB）になり初回起動も 5 秒近くかかるため採用していません。

### SmartScreen の警告について

コード署名をしていないため、初回起動時に「WindowsによってPCが保護されました」と表示されます。`詳細情報` → `実行` で起動できます。気になる場合はソースから自分でビルドしてください。

## ビルド

```
dotnet test tests/PastelNet.Core.Tests/PastelNet.Core.Tests.csproj
dotnet publish src/PastelNet.App/PastelNet.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

`PastelNet.exe --selftest` で UI を出さずに自己診断だけ実行し、終了コードを返します（CI で使っています）。

## ライセンス

MIT
