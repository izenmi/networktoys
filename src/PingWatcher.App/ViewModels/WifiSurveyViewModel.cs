using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PingWatcher.App.Mvvm;
using PingWatcher.App.Services;
using PingWatcher.Core.Survey;

namespace PingWatcher.App.ViewModels;

/// <summary>ヒートマップの描画対象 1 つ（ComboBox の項目）。</summary>
public sealed class HeatmapChoiceViewModel
{
    public HeatmapChoiceViewModel(string label, HeatmapSource source)
    {
        Label = label;
        Source = source;
    }

    public string Label { get; }
    public HeatmapSource Source { get; }
}

/// <summary>
/// 無線サーベイ画面。フロア図の上をクリックして測定点を打ち、
/// 各点で見えた AP の RSSI からヒートマップを作る。
///
/// <b>クリック = その場でスキャン（約 4 秒）</b>を正とする。OS の BSS キャッシュ
/// （最大 60 秒前）を紐づけると「別の場所で測った値」になり、位置と値の対応が
/// 壊れるため。定期スキャンのループは持たないので、タブの Activate/Deactivate
/// 連動も要らない。
///
/// コンストラクタは WLAN API・ファイル・ダイアログに一切触れない
/// （自己診断が画面を実体化して検査するための前提）。
/// </summary>
public sealed class WifiSurveyViewModel : ObservableObject
{
    public const int GridWidth = 128;

    /// <summary>位置情報が拒否されていて BSSID が分からないときの疑似 BSSID。</summary>
    private const string UnknownBssid = "(接続中)";

    private SurveyDocument _document = NewDocument();
    private CancellationTokenSource? _cts;

    private string _surveyName = "";
    private string _status = "フロア図を開くか方眼のまま、図の上をクリックすると測定します（1 点ごとに約 4 秒のスキャン）。";
    private bool _isMeasuring;
    private bool _isDenied;
    private string? _floorImagePath;
    private float[]? _heatGrid;
    private int _gridHeight = 96;
    private HeatmapChoiceViewModel? _selectedChoice;
    private double _heatOpacity = 0.6;
    private (double X, double Y)? _pendingPoint;

    public WifiSurveyViewModel()
    {
        HeatmapChoices =
        [
            new HeatmapChoiceViewModel("接続中の AP", new HeatmapSource(HeatmapMode.Connected, null)),
            new HeatmapChoiceViewModel("最強値", new HeatmapSource(HeatmapMode.Strongest, null)),
        ];
        _selectedChoice = HeatmapChoices[0];

        LoadImageCommand = new RelayCommand(LoadImage);
        UseGridCommand = new RelayCommand(UseGrid);
        SaveCommand = new RelayCommand(Save);
        LoadCommand = new RelayCommand(Load);
        ClearPointsCommand = new RelayCommand(ClearPoints);
        OpenLocationSettingsCommand = new RelayCommand(OpenLocationSettings);
    }

    /// <summary>描画側（SurveyCanvas）へ「描き直せ」を伝える。</summary>
    public event EventHandler? SurveyChanged;

    public ObservableCollection<HeatmapChoiceViewModel> HeatmapChoices { get; }

    public RelayCommand LoadImageCommand { get; }
    public RelayCommand UseGridCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand ClearPointsCommand { get; }
    public RelayCommand OpenLocationSettingsCommand { get; }

    public string SurveyName
    {
        get => _surveyName;
        set => SetProperty(ref _surveyName, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set => SetProperty(ref _isMeasuring, value);
    }

    /// <summary>位置情報が許可されていない状態か。案内バナーの表示切り替えに使う。</summary>
    public bool IsDenied
    {
        get => _isDenied;
        private set => SetProperty(ref _isDenied, value);
    }

    /// <summary>フロア図の絶対パス。null なら方眼。</summary>
    public string? FloorImagePath
    {
        get => _floorImagePath;
        private set => SetProperty(ref _floorImagePath, value);
    }

    public double AspectRatio => _document.AspectRatio;

    public IReadOnlyList<SurveyPoint> Points => _document.Points;

    /// <summary>測定中でまだ確定していない点（輪郭だけで描く）。</summary>
    public (double X, double Y)? PendingPoint => _pendingPoint;

    /// <summary>補間済みグリッド（row-major、NaN=未測定）。点が無ければ null。</summary>
    public float[]? HeatGrid => _heatGrid;

    public int GridHeight => _gridHeight;

    public HeatmapChoiceViewModel? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (SetProperty(ref _selectedChoice, value))
                RebuildHeatmap();
        }
    }

    public HeatmapSource CurrentSource => _selectedChoice?.Source ?? new HeatmapSource(HeatmapMode.Connected, null);

    public double HeatOpacity
    {
        get => _heatOpacity;
        set
        {
            if (SetProperty(ref _heatOpacity, value))
                RaiseSurveyChanged();
        }
    }

    /// <summary>クリック位置（0..1）で測定して点を確定する。SurveyCanvas から呼ばれる。</summary>
    public async Task AddPointAtAsync(double x, double y)
    {
        if (IsMeasuring)
            return;

        IsMeasuring = true;
        _pendingPoint = (x, y);
        Status = "測定中…（約 4 秒かかります。その場で止まっていてください）";
        RaiseSurveyChanged();

        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            // 接続中 AP の RSSI はスキャンを待たずその場の値を取る
            // （BSS 一覧の値より新しく、位置ズレが無い）
            Task<int?> rssiTask = Task.Run(WifiService.GetRssi);
            WifiSnapshot snapshot = await WifiService.ScanAsync(_cts.Token);
            int? freshRssi = await rssiTask;

            var point = new SurveyPoint { X = x, Y = y, MeasuredAt = DateTimeOffset.Now };

            if (snapshot.Failure == WifiFailure.None)
            {
                IsDenied = false;
                point.ConnectedBssid = snapshot.Connection?.Bssid;

                foreach (WifiAccessPoint ap in snapshot.AccessPoints)
                {
                    int rssi = ap.IsConnected && freshRssi is int fresh ? fresh : ap.Rssi;
                    point.Readings.Add(new SurveyReading
                    {
                        Ssid = ap.Ssid,
                        Bssid = ap.Bssid,
                        Rssi = rssi,
                        Channel = ap.Channel,
                        Band = ap.Band,
                    });
                }
            }
            else if (snapshot.Failure == WifiFailure.LocationDenied)
            {
                // BSS 一覧は位置情報の同意が要るが、接続中の RSSI だけは取れる。
                // 接続中 AP のサーベイとしては成立するので、1 行だけ記録して続ける
                IsDenied = true;

                if (freshRssi is not int rssiOnly)
                {
                    Status = "位置情報が許可されておらず、受信強度も取得できませんでした。";
                    return;
                }

                point.ConnectedBssid = UnknownBssid;
                point.Readings.Add(new SurveyReading { Ssid = "(接続中の AP)", Bssid = UnknownBssid, Rssi = rssiOnly });
            }
            else
            {
                Status = snapshot.Message ?? "スキャンできませんでした。";
                return;
            }

            if (point.Readings.Count == 0)
            {
                Status = "AP が 1 つも見えませんでした。点は打たずに続けます。";
                return;
            }

            if (_document.CreatedAt == default)
                _document.CreatedAt = DateTimeOffset.Now;

            _document.Points.Add(point);
            UpdateChoices(point);
            RebuildHeatmap();
            Status = $"{point.Readings.Count} AP を記録しました（{_document.Points.Count} 点目）。";
        }
        catch (Exception ex)
        {
            Status = "測定に失敗しました。";
            CrashLog.Write(ex, "WifiSurveyViewModel.AddPointAtAsync");
        }
        finally
        {
            _pendingPoint = null;
            IsMeasuring = false;
            RaiseSurveyChanged();
        }
    }

    /// <summary>右クリックで近くの点を消す。tolerance は正規化座標。</summary>
    public void RemovePointNear(double x, double y, double tolerance)
    {
        SurveyPoint? nearest = null;
        double best = tolerance * tolerance;

        foreach (SurveyPoint point in _document.Points)
        {
            double dx = point.X - x;
            double dy = point.Y - y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= best)
            {
                best = distanceSquared;
                nearest = point;
            }
        }

        if (nearest is null)
            return;

        _document.Points.Remove(nearest);
        RebuildHeatmap();
        Status = $"測定点を 1 つ消しました（残り {_document.Points.Count} 点）。";
        RaiseSurveyChanged();
    }

    /// <summary>すべて捨てて起動直後の状態へ（クリア一括用）。</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _document = NewDocument();
        FloorImagePath = null;
        SurveyName = "";
        _heatGrid = null;
        ResetChoices();
        IsDenied = false;
        Status = "フロア図を開くか方眼のまま、図の上をクリックすると測定します（1 点ごとに約 4 秒のスキャン）。";
        OnPropertyChanged(nameof(AspectRatio));
        RaiseSurveyChanged();
    }

    private void LoadImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "画像 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|すべてのファイル (*.*)|*.*",
            Title = "フロア図を開く",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            if (_document.CreatedAt == default)
                _document.CreatedAt = DateTimeOffset.Now;

            // USB で持ち出しても図がついて来るよう、surveys フォルダへコピーして
            // ファイル名だけを JSON に持たせる
            string directory = AppData.PathOf("surveys");
            Directory.CreateDirectory(directory);

            string extension = Path.GetExtension(dialog.FileName);
            string fileName = Path.GetFileNameWithoutExtension(
                SurveyStore.BuildFileName(_document.CreatedAt, SurveyName)) + "-floor" + extension;
            string copied = Path.Combine(directory, fileName);
            File.Copy(dialog.FileName, copied, overwrite: true);

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(copied);
            image.CacheOption = BitmapCacheOption.OnLoad;   // 読み込み後にファイルをロックしない
            image.EndInit();
            image.Freeze();

            _document.FloorImageFile = fileName;
            _document.AspectRatio = image.PixelHeight > 0
                ? image.PixelWidth / (double)image.PixelHeight
                : 4.0 / 3.0;
            FloorImagePath = copied;

            Status = $"{Path.GetFileName(dialog.FileName)} を敷きました。図の上をクリックすると測定します。";
            OnPropertyChanged(nameof(AspectRatio));
            RebuildHeatmap();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
        {
            Status = $"フロア図を読み込めませんでした: {ex.Message}";
        }
    }

    private void UseGrid()
    {
        _document.FloorImageFile = null;
        _document.AspectRatio = 4.0 / 3.0;
        FloorImagePath = null;
        Status = "方眼に切り替えました。図の上をクリックすると測定します。";
        OnPropertyChanged(nameof(AspectRatio));
        RebuildHeatmap();
    }

    private void Save()
    {
        if (_document.Points.Count == 0)
        {
            Status = "測定点がまだありません。";
            return;
        }

        try
        {
            if (_document.CreatedAt == default)
                _document.CreatedAt = DateTimeOffset.Now;
            _document.Name = SurveyName.Trim();

            string directory = AppData.PathOf("surveys");
            string path = Path.Combine(directory, SurveyStore.BuildFileName(_document.CreatedAt, _document.Name));
            SurveyStore.Save(path, _document);

            Status = $"{Path.GetFileName(path)} に保存しました（{_document.Points.Count} 点）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    private void Load()
    {
        string directory = AppData.PathOf("surveys");

        var dialog = new OpenFileDialog
        {
            Filter = "サーベイ (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "サーベイを開く",
            InitialDirectory = Directory.Exists(directory) ? directory : "",
        };

        if (dialog.ShowDialog() != true)
            return;

        SurveyDocument? loaded = SurveyStore.Load(dialog.FileName, out string? error);
        if (loaded is null)
        {
            Status = error ?? "サーベイを読み込めませんでした。";
            return;
        }

        _document = loaded;
        SurveyName = loaded.Name;

        // 画像は JSON と同じフォルダから引く。見つからなければ方眼で開く
        FloorImagePath = null;
        if (loaded.FloorImageFile is { Length: > 0 } imageFile)
        {
            string imagePath = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? "", imageFile);
            if (File.Exists(imagePath))
                FloorImagePath = imagePath;
            else
                Status = $"フロア図 {imageFile} が見つからないため、方眼で表示します。";
        }

        ResetChoices();
        foreach (SurveyPoint point in loaded.Points)
            UpdateChoices(point);

        RebuildHeatmap();
        if (FloorImagePath is not null || loaded.FloorImageFile is null)
            Status = $"{Path.GetFileName(dialog.FileName)} を開きました（{loaded.Points.Count} 点）。続きから測定できます。";

        OnPropertyChanged(nameof(AspectRatio));
        RaiseSurveyChanged();
    }

    private void ClearPoints()
    {
        if (_document.Points.Count == 0)
            return;

        _document.Points.Clear();
        _heatGrid = null;
        ResetChoices();
        Status = "測定点をすべて消しました（フロア図は残しています）。";
        RaiseSurveyChanged();
    }

    private void RebuildHeatmap()
    {
        _gridHeight = Math.Max(8, (int)Math.Round(GridWidth / Math.Max(0.2, _document.AspectRatio)));

        var samples = new List<SamplePoint>(_document.Points.Count);
        HeatmapSource source = CurrentSource;

        foreach (SurveyPoint point in _document.Points)
        {
            if (Heatmap.SelectValue(point.Readings, point.ConnectedBssid, source) is double value)
                samples.Add(new SamplePoint(point.X, point.Y, value));
        }

        _heatGrid = samples.Count > 0
            ? Heatmap.Interpolate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), GridWidth, _gridHeight)
            : null;

        OnPropertyChanged(nameof(GridHeight));
        RaiseSurveyChanged();
    }

    private void UpdateChoices(SurveyPoint point)
    {
        foreach (SurveyReading reading in point.Readings)
        {
            if (reading.Bssid.Length == 0 || reading.Bssid == UnknownBssid)
                continue;

            bool known = HeatmapChoices.Any(c =>
                c.Source.Mode == HeatmapMode.SingleBssid &&
                string.Equals(c.Source.Bssid, reading.Bssid, StringComparison.OrdinalIgnoreCase));
            if (known)
                continue;

            string ssid = reading.Ssid.Length > 0 ? reading.Ssid : "(ステルス)";
            HeatmapChoices.Add(new HeatmapChoiceViewModel(
                $"{ssid} ({reading.Bssid})",
                new HeatmapSource(HeatmapMode.SingleBssid, reading.Bssid)));
        }
    }

    private void ResetChoices()
    {
        while (HeatmapChoices.Count > 2)
            HeatmapChoices.RemoveAt(HeatmapChoices.Count - 1);
        SelectedChoice = HeatmapChoices[0];
    }

    private void RaiseSurveyChanged() => SurveyChanged?.Invoke(this, EventArgs.Empty);

    private static SurveyDocument NewDocument() => new();

    private static void OpenLocationSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:privacy-location",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "WifiSurveyViewModel.OpenLocationSettings");
        }
    }
}
