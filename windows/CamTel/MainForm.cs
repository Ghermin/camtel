using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CamTel;

public sealed class MainForm : Form
{
    private const int PhonePort = 4747;
    private const int UsbLocalPort = 4747;
    private const string RepoUrl = "https://github.com/Ghermin/camtel";

    private static readonly Color Bg = Color.FromArgb(22, 26, 36);
    private static readonly Color Panel = Color.FromArgb(32, 37, 50);
    private static readonly Color Muted = Color.FromArgb(150, 160, 180);

    private static readonly (string Label, int W, int H)[] OutputSizes =
    {
        ("640 × 480", 640, 480), ("1280 × 720", 1280, 720), ("1920 × 1080", 1920, 1080),
    };
    private static readonly string[] PhoneResolutions = { "640x480", "1280x720", "1920x1080" };

    private readonly Settings _settings = Settings.Load();
    private readonly FramePipeline _pipeline;
    private readonly PhoneDiscovery _discovery = new();
    private readonly List<Phone> _usbPhones = new();

    private CancellationTokenSource? _connectCts;
    private Phone? _connected;
    private Uri? _apiBase;
    private volatile bool _streaming;
    private Image? _previewImage;
    private bool _reallyClose;
    private string _link = "Non connecté";

    // Phone state, as last reported by the phone.
    private bool _front, _torch, _hasFlash = true, _mirror, _blackout, _night;
    private double _exposureMin = -2, _exposureMax = 2;
    private int _resIndex = 1;
    private bool _draggingZoom, _draggingQuality, _draggingExposure, _applyingStatus;

    // UI
    private readonly ComboBox _phoneBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown, Width = 320, FlatStyle = FlatStyle.Flat,
        BackColor = Panel, ForeColor = Color.White,
    };
    private readonly Button _connectBtn = new()
    {
        Text = "Connecter", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = IconButton.Accent,
        ForeColor = Color.White, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(8, 0, 0, 0),
    };
    private readonly IconButton _moreBtn = new(Icons.More) { Size = new Size(36, 36) };
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom,
    };
    private readonly IconButton _switchBtn = new(Icons.Switch);
    private readonly IconButton _torchBtn = new(Icons.FlashOff);
    private readonly IconButton _mirrorBtn = new(Icons.Flip);
    private readonly IconButton _resBtn = new(null, "720p");
    private readonly IconButton _fpsBtn = new(null, "30 ips") { Font = new Font("Segoe UI Semibold", 8.5f) };
    private readonly IconButton _blackBtn = new(Icons.Dark);
    private readonly IconButton _rotateBtn = new(Icons.Rotate);
    private readonly IconButton _fillBtn = new(Icons.Crop);
    private readonly TrackBar _zoomBar = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Width = 160, BackColor = Bg };
    private readonly Label _zoomLabel = new() { AutoSize = true, ForeColor = Color.White, Text = "0 %", Margin = new Padding(0, 6, 18, 0) };
    private readonly TrackBar _exposureBar = new() { Minimum = 0, Maximum = 100, Value = 50, TickStyle = TickStyle.None, Width = 160, BackColor = Bg };
    private readonly Label _exposureLabel = new() { AutoSize = true, ForeColor = Color.White, Text = "+0,0 EV", Margin = new Padding(0, 6, 18, 0) };
    private readonly TrackBar _qualityBar = new() { Minimum = 30, Maximum = 100, Value = 80, TickStyle = TickStyle.None, Width = 140, BackColor = Bg };
    private readonly Label _qualityLabel = new() { AutoSize = true, ForeColor = Color.White, Text = "80", Margin = new Padding(0, 6, 18, 0) };
    private readonly ComboBox _outputBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat,
        BackColor = Panel, ForeColor = Color.White,
    };
    private readonly Label _linkLabel = new() { AutoSize = true, ForeColor = Color.White };
    private readonly Label _camLabel = new() { AutoSize = true, ForeColor = Muted, Dock = DockStyle.Right };
    private readonly ToolTip _tips = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _usbTimer = new() { Interval = 3000 };

    public MainForm()
    {
        Text = "CamTel";
        Icon = LoadIcon();
        MinimumSize = new Size(900, 560);
        Size = new Size(1120, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;
        ForeColor = Color.White;

        _pipeline = new FramePipeline(_settings.OutputWidth, _settings.OutputHeight)
        {
            Rotation = _settings.Rotation,
            Fill = _settings.Fill,
        };
        _pipeline.Preview += OnPreview;

        BuildUi();
        WireEvents();
        RenderPhoneState();
        RenderPcState();

        _discovery.Changed += () => BeginInvokeSafe(RefreshPhoneList);
        _discovery.Start();
        _statusTimer.Start();
        _usbTimer.Start();
    }

    // ---- Layout -------------------------------------------------------------

    private void BuildUi()
    {
        // Top bar: phone picker + connect + menu.
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 52, ColumnCount = 3, Padding = new Padding(12, 8, 12, 6), BackColor = Bg,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var picker = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        picker.Controls.Add(new Label { Text = "Téléphone", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 7, 8, 0) });
        picker.Controls.Add(_phoneBox);
        picker.Controls.Add(_connectBtn);
        top.Controls.Add(picker, 0, 0);
        top.Controls.Add(new Label(), 1, 0);
        top.Controls.Add(_moreBtn, 2, 0);

        // Right column: same buttons as on the phone, then PC-only output options.
        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 80, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(12, 4, 12, 4), BackColor = Bg, AutoScroll = true,
        };
        side.Controls.Add(SectionLabel("Tél."));
        foreach (var b in new[] { _switchBtn, _torchBtn, _mirrorBtn, _resBtn, _fpsBtn, _blackBtn }) side.Controls.Add(b);
        side.Controls.Add(SectionLabel("PC"));
        side.Controls.Add(_rotateBtn);
        side.Controls.Add(_fillBtn);

        // Bottom bar: zoom / quality / output size.
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, WrapContents = false, Padding = new Padding(12, 6, 12, 0), BackColor = Bg,
        };
        bottom.Controls.Add(new PictureBox
        {
            Size = new Size(22, 22), Margin = new Padding(0, 4, 4, 0), Image = RenderIcon(Icons.Zoom, 22),
        });
        bottom.Controls.Add(_zoomBar);
        bottom.Controls.Add(_zoomLabel);
        bottom.Controls.Add(new PictureBox
        {
            Size = new Size(22, 22), Margin = new Padding(0, 4, 4, 0), Image = RenderIcon(Icons.Brightness, 22),
        });
        bottom.Controls.Add(_exposureBar);
        bottom.Controls.Add(_exposureLabel);
        bottom.Controls.Add(Caption("Qualité"));
        bottom.Controls.Add(_qualityBar);
        bottom.Controls.Add(_qualityLabel);
        bottom.Controls.Add(Caption("Sortie webcam"));
        bottom.Controls.Add(_outputBox);

        var status = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(12, 5, 12, 0), BackColor = Panel };
        status.Controls.Add(_linkLabel);
        status.Controls.Add(_camLabel);

        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), BackColor = Bg };
        previewHost.Controls.Add(_preview);

        Controls.Add(previewHost);
        Controls.Add(side);
        Controls.Add(bottom);
        Controls.Add(top);
        Controls.Add(status);

        _connectBtn.FlatAppearance.BorderSize = 0;
        foreach (var o in OutputSizes) _outputBox.Items.Add(o.Label);
        var outIdx = Array.FindIndex(OutputSizes, o => o.W == _settings.OutputWidth && o.H == _settings.OutputHeight);
        _outputBox.SelectedIndex = outIdx < 0 ? 1 : outIdx;
        if (_settings.LastHost.Length > 0 && !_settings.LastUsb) _phoneBox.Text = _settings.LastHost;

        _tips.SetToolTip(_switchBtn, "Caméra avant / arrière");
        _tips.SetToolTip(_torchBtn, "Flash");
        _tips.SetToolTip(_mirrorBtn, "Miroir (sur le téléphone et la webcam)");
        _tips.SetToolTip(_resBtn, "Résolution du téléphone");
        _tips.SetToolTip(_fpsBtn, "30 ips (fluide) ou mode nuit (plus lumineux, moins fluide)");
        _tips.SetToolTip(_exposureBar, "Luminosité (compensation d'exposition)");
        _tips.SetToolTip(_blackBtn, "Éteindre l'écran du téléphone (la caméra continue)");
        _tips.SetToolTip(_rotateBtn, "Pivoter l'image de la webcam");
        _tips.SetToolTip(_fillBtn, "Remplir le cadre (rogne au lieu d'ajouter des bandes noires)");
        _tips.SetToolTip(_outputBox, "Résolution annoncée par « CamTel Camera » aux applis");
        _tips.SetToolTip(_moreBtn, "Menu");

        _moreMenu.Items.Add("Installer / réparer la webcam virtuelle", null, (_, _) => InstallDriver(force: true));
        _moreMenu.Items.Add("Désinstaller la webcam virtuelle", null, (_, _) => UninstallDriver());
        _moreMenu.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("Connexion auto au démarrage") { Checked = _settings.AutoConnect, CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { _settings.AutoConnect = auto.Checked; _settings.Save(); };
        _moreMenu.Items.Add(auto);
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add("Télécharger l'app Android", null, (_, _) => OpenUrl(RepoUrl + "/releases/latest"));
        _moreMenu.Items.Add("Page GitHub", null, (_, _) => OpenUrl(RepoUrl));
        _moreMenu.Items.Add("À propos", null, (_, _) => MessageBox.Show(this,
            $"CamTel {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}\n\n" +
            "Utilise ton téléphone Android comme webcam.\n" +
            "Caméra virtuelle basée sur softcam (MIT).\n\n" + RepoUrl, "À propos de CamTel"));

        _tray.Icon = Icon;
        _tray.Text = "CamTel";
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add("Afficher", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => { _reallyClose = true; Close(); });
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text, AutoSize = false, Width = 46, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted,
        Margin = new Padding(5, 10, 5, 0), Height = 18,
    };

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 6, 4, 0),
    };

    private static Bitmap RenderIcon(string path, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 24f, size / 24f);
        using var p = SvgPath.Parse(path);
        g.FillPath(new SolidBrush(Muted), p);
        return bmp;
    }

    private void WireEvents()
    {
        _connectBtn.Click += (_, _) =>
        {
            if (_connectCts != null) Disconnect();
            else ConnectToSelection();
        };
        _phoneBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Disconnect(); ConnectToSelection(); }
        };
        _moreBtn.Click += (_, _) => _moreMenu.Show(_moreBtn, new Point(0, _moreBtn.Height));

        _switchBtn.Click += (_, _) => SendControl("camera=" + (_front ? "back" : "front"));
        _torchBtn.Click += (_, _) => SendControl("torch=" + (_torch ? "off" : "on"));
        _mirrorBtn.Click += (_, _) => SendControl("mirror=" + (_mirror ? "off" : "on"));
        _resBtn.Click += (_, _) => SendControl("res=" + PhoneResolutions[(_resIndex + 1) % PhoneResolutions.Length]);
        _blackBtn.Click += (_, _) => SendControl("blackout=" + (_blackout ? "off" : "on"));
        _fpsBtn.Click += (_, _) => SendControl("night=" + (_night ? "off" : "on"));
        _exposureBar.MouseDown += (_, _) => _draggingExposure = true;
        _exposureBar.MouseUp += (_, _) => _draggingExposure = false;
        _exposureBar.ValueChanged += (_, _) =>
        {
            var ev = _exposureMin + (_exposureMax - _exposureMin) * _exposureBar.Value / 100.0;
            _exposureLabel.Text = ev.ToString("+0.0;-0.0;+0.0", CultureInfo.GetCultureInfo("fr-FR")) + " EV";
            if (!_applyingStatus) SendControl("exposure=" + ev.ToString("0.00", CultureInfo.InvariantCulture));
        };

        _zoomBar.MouseDown += (_, _) => _draggingZoom = true;
        _zoomBar.MouseUp += (_, _) => _draggingZoom = false;
        _zoomBar.ValueChanged += (_, _) =>
        {
            _zoomLabel.Text = $"{_zoomBar.Value} %";
            if (!_applyingStatus)
                SendControl("zoom=" + (_zoomBar.Value / 100f).ToString(CultureInfo.InvariantCulture));
        };
        _qualityBar.MouseDown += (_, _) => _draggingQuality = true;
        _qualityBar.MouseUp += (_, _) =>
        {
            _draggingQuality = false;
            SendControl("quality=" + _qualityBar.Value);
        };
        _qualityBar.ValueChanged += (_, _) => _qualityLabel.Text = _qualityBar.Value.ToString();
        _qualityBar.KeyUp += (_, _) => { if (!_applyingStatus) SendControl("quality=" + _qualityBar.Value); };

        _rotateBtn.Click += (_, _) =>
        {
            _settings.Rotation = (_settings.Rotation + 90) % 360;
            _pipeline.Rotation = _settings.Rotation;
            _settings.Save();
            RenderPcState();
        };
        _fillBtn.Click += (_, _) =>
        {
            _settings.Fill = !_settings.Fill;
            _pipeline.Fill = _settings.Fill;
            _settings.Save();
            RenderPcState();
        };
        _outputBox.SelectedIndexChanged += (_, _) =>
        {
            var o = OutputSizes[_outputBox.SelectedIndex];
            if (o.W == _settings.OutputWidth && o.H == _settings.OutputHeight) return;
            _settings.OutputWidth = o.W;
            _settings.OutputHeight = o.H;
            _pipeline.SetOutputSize(o.W, o.H);
            _settings.Save();
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _usbTimer.Tick += async (_, _) => await RefreshUsbAsync();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _streaming)
            {
                Hide();
                _tray.Visible = true;
                _tray.ShowBalloonTip(2000, "CamTel", "CamTel continue en arrière-plan.", ToolTipIcon.Info);
            }
        };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureDriver();
        await RefreshUsbAsync();
        if (_settings.AutoConnect && _settings.LastHost.Length > 0)
        {
            if (_settings.LastUsb)
            {
                var usb = _usbPhones.FirstOrDefault(p => p.Host == _settings.LastHost) ?? _usbPhones.FirstOrDefault();
                if (usb != null) Connect(usb);
            }
            else
            {
                Connect(new Phone(_settings.LastHost, _settings.LastHost, PhonePort, false));
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing && _streaming)
        {
            var r = MessageBox.Show(this,
                "Une connexion est active. Quitter CamTel coupera la webcam.\n\nQuitter quand même ? (Non = réduire près de l'horloge)",
                "CamTel", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.No)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                return;
            }
        }
        _connectCts?.Cancel();
        _pipeline.Preview -= OnPreview;
        _pipeline.Dispose();
        _discovery.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    // ---- Driver -------------------------------------------------------------

    private void EnsureDriver()
    {
        if (!DriverInstaller.HasEmbeddedDriver())
        {
            if (!DriverInstaller.IsInstalled)
                MessageBox.Show(this, "Cette version de CamTel ne contient pas le pilote de webcam virtuelle.",
                    "CamTel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (DriverInstaller.IsInstalled && !DriverInstaller.NeedsUpdate()) return;

        var msg = DriverInstaller.IsInstalled
            ? "Une nouvelle version de la webcam virtuelle « CamTel Camera » est disponible. La mettre à jour ?"
            : "Pour que Zoom, Teams, Discord, OBS, Meet… voient ton téléphone, CamTel doit installer la webcam virtuelle « CamTel Camera ».\n\nWindows va demander l'autorisation administrateur. Installer maintenant ?";
        if (MessageBox.Show(this, msg, "CamTel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            InstallDriver(force: false);
    }

    private void InstallDriver(bool force)
    {
        if (!force && DriverInstaller.IsInstalled && !DriverInstaller.NeedsUpdate()) return;
        if (DriverInstaller.RunElevated(install: true))
        {
            _pipeline.ResetCamera();
            MessageBox.Show(this,
                "Webcam virtuelle « CamTel Camera » installée.\nSi une appli était déjà ouverte (navigateur, Teams…), relance-la pour qu'elle la voie.",
                "CamTel", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, "L'installation de la webcam virtuelle a échoué ou a été annulée.", "CamTel",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UninstallDriver()
    {
        if (MessageBox.Show(this, "Désinstaller la webcam virtuelle « CamTel Camera » ?", "CamTel",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (DriverInstaller.RunElevated(install: false))
            MessageBox.Show(this, "Webcam virtuelle désinstallée.", "CamTel");
    }

    // ---- Phones -------------------------------------------------------------

    private async Task RefreshUsbAsync()
    {
        if (Adb.FindAdb() == null) return;
        var devices = await Adb.DevicesAsync();
        var phones = devices.Select(d => new Phone(d.Model, d.Serial, PhonePort, true)).ToList();
        if (phones.SequenceEqual(_usbPhones)) return;
        _usbPhones.Clear();
        _usbPhones.AddRange(phones);
        RefreshPhoneList();
    }

    private void RefreshPhoneList()
    {
        var typed = _phoneBox.SelectedItem == null ? _phoneBox.Text : null;
        var selected = _phoneBox.SelectedItem as Phone;
        _phoneBox.BeginUpdate();
        _phoneBox.Items.Clear();
        foreach (var p in _usbPhones.Concat(_discovery.Phones)) _phoneBox.Items.Add(p);
        _phoneBox.EndUpdate();
        if (selected != null && _phoneBox.Items.Contains(selected)) _phoneBox.SelectedItem = selected;
        else if (!string.IsNullOrEmpty(typed)) _phoneBox.Text = typed;
        else if (_phoneBox.Items.Count > 0 && _connectCts == null) _phoneBox.SelectedIndex = 0;
    }

    private void ConnectToSelection()
    {
        if (_phoneBox.SelectedItem is Phone p)
        {
            Connect(p);
            return;
        }
        var text = _phoneBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show(this,
                "Aucun téléphone détecté.\n\n• Ouvre l'app CamTel sur le téléphone (même Wi-Fi que le PC), ou\n• branche-le en USB avec le débogage USB activé, ou\n• tape l'adresse IP affichée dans l'app.",
                "CamTel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var host = text;
        var port = PhonePort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsed))
        {
            host = text[..colon];
            port = parsed;
        }
        Connect(new Phone(host, host, port, false));
    }

    private void Connect(Phone phone)
    {
        Disconnect();
        _connected = phone;
        _settings.LastHost = phone.Host;
        _settings.LastUsb = phone.Usb;
        _settings.Save();
        _connectBtn.Text = "Déconnecter";
        _phoneBox.Enabled = false;
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        _ = ConnectLoopAsync(phone, cts.Token);
    }

    private void Disconnect()
    {
        _connectCts?.Cancel();
        _connectCts = null;
        _connected = null;
        _apiBase = null;
        _streaming = false;
        _connectBtn.Text = "Connecter";
        _phoneBox.Enabled = true;
        _pipeline.Placeholder = "En attente du téléphone…";
        RenderPhoneState();
    }

    private async Task ConnectLoopAsync(Phone phone, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var host = phone.Host;
            var port = phone.Port;
            if (phone.Usb)
            {
                SetLink($"USB : redirection du port vers {phone.Name}…");
                if (!await Adb.ForwardAsync(phone.Host, UsbLocalPort, phone.Port))
                {
                    SetLink("USB : téléphone introuvable (câble branché ? débogage USB autorisé ?)");
                    if (!await DelayAsync(2000, ct)) return;
                    continue;
                }
                host = "127.0.0.1";
                port = UsbLocalPort;
            }

            var api = new Uri($"http://{host}:{port}/");
            SetLink($"Connexion à {phone.Name}…");
            _pipeline.Placeholder = $"Connexion à {phone.Name}…";
            var status = await MjpegClient.GetStringAsync(new Uri(api, "status"), ct);
            if (status == null)
            {
                SetLink($"{phone.Name} ne répond pas. L'app CamTel est-elle ouverte sur le téléphone ?");
                _pipeline.Placeholder = "Ouvre l'app CamTel sur le téléphone";
                if (!await DelayAsync(2000, ct)) return;
                continue;
            }

            _apiBase = api;
            BeginInvokeSafe(() => ApplyPhoneStatus(status));
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = StatusLoopAsync(api, session.Token);
            try
            {
                _streaming = true;
                SetLink($"Connecté à {phone.Name}");
                await MjpegClient.StreamAsync(new Uri(api, "video"), _pipeline.SubmitJpeg, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _streaming = false;
                session.Cancel();
            }
            if (ct.IsCancellationRequested) return;
            SetLink($"Connexion perdue avec {phone.Name}, nouvelle tentative…");
            _pipeline.Placeholder = "Connexion perdue, reconnexion…";
            if (!await DelayAsync(1500, ct)) return;
        }
    }

    /// <summary>Long-polls /status so changes made on the phone show up here immediately.</summary>
    private async Task StatusLoopAsync(Uri api, CancellationToken ct)
    {
        long version = 0;
        while (!ct.IsCancellationRequested)
        {
            var json = await MjpegClient.GetStringAsync(new Uri(api, $"status?since={version}"), ct,
                TimeSpan.FromSeconds(30));
            if (ct.IsCancellationRequested) return;
            if (json == null)
            {
                if (!await DelayAsync(1000, ct)) return;
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                version = doc.RootElement.GetProperty("version").GetInt64();
            }
            catch
            {
                if (!await DelayAsync(1000, ct)) return;
                continue;
            }
            BeginInvokeSafe(() => ApplyPhoneStatus(json));
        }
    }

    private static async Task<bool> DelayAsync(int ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async void SendControl(string query)
    {
        var api = _apiBase;
        if (api == null) return;
        // The phone applies the change and the status long-poll brings the new state back.
        await MjpegClient.GetStringAsync(new Uri(api, "control?" + query));
    }

    private void ApplyPhoneStatus(string json)
    {
        _applyingStatus = true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _front = r.GetProperty("camera").GetString() == "front";
            _torch = r.GetProperty("torch").GetBoolean();
            _hasFlash = !r.TryGetProperty("hasFlash", out var hf) || hf.GetBoolean();
            _mirror = r.TryGetProperty("mirror", out var m) && m.GetBoolean();
            _blackout = r.TryGetProperty("blackout", out var bo) && bo.GetBoolean();
            _night = r.TryGetProperty("night", out var ni) && ni.GetBoolean();
            if (r.TryGetProperty("exposureMin", out var emin) && r.TryGetProperty("exposureMax", out var emax))
            {
                _exposureMin = emin.GetDouble();
                _exposureMax = emax.GetDouble();
                var span = _exposureMax - _exposureMin;
                var ev = r.GetProperty("exposure").GetDouble();
                if (!_draggingExposure)
                    _exposureBar.Value = span > 0 ? Math.Clamp((int)Math.Round((ev - _exposureMin) / span * 100), 0, 100) : 50;
                _exposureLabel.Text = ev.ToString("+0.0;-0.0;+0.0", CultureInfo.GetCultureInfo("fr-FR")) + " EV";
            }
            var idx = Array.IndexOf(PhoneResolutions, r.GetProperty("resolution").GetString());
            if (idx >= 0) _resIndex = idx;
            if (!_draggingQuality)
                _qualityBar.Value = Math.Clamp(r.GetProperty("quality").GetInt32(), _qualityBar.Minimum, _qualityBar.Maximum);
            if (!_draggingZoom)
                _zoomBar.Value = Math.Clamp((int)Math.Round(r.GetProperty("zoom").GetDouble() * 100), 0, 100);
            _pipeline.Mirror = _mirror;
        }
        catch
        {
        }
        finally
        {
            _applyingStatus = false;
        }
        RenderPhoneState();
    }

    private void RenderPhoneState()
    {
        var on = _apiBase != null;
        foreach (Control c in new Control[] { _switchBtn, _torchBtn, _mirrorBtn, _resBtn, _fpsBtn, _blackBtn, _zoomBar, _exposureBar, _qualityBar })
            c.Enabled = on;
        _torchBtn.Enabled = on && _hasFlash;
        _switchBtn.Active = on && _front;
        _torchBtn.Active = on && _torch;
        _torchBtn.IconPath = _torch ? Icons.FlashOn : Icons.FlashOff;
        _mirrorBtn.Active = on && _mirror;
        _blackBtn.Active = on && _blackout;
        _fpsBtn.Active = on && _night;
        _fpsBtn.Text = _night ? "Nuit" : "30 ips";
        _exposureBar.Enabled = on && _exposureMax > _exposureMin;
        _resBtn.Text = PhoneResolutions[_resIndex].Split('x')[1] + "p";
    }

    private void RenderPcState()
    {
        _rotateBtn.Active = _settings.Rotation != 0;
        _tips.SetToolTip(_rotateBtn, $"Pivoter l'image de la webcam (actuellement {_settings.Rotation}°)");
        _fillBtn.Active = _settings.Fill;
    }

    // ---- Status / preview ---------------------------------------------------

    private void SetLink(string text) => BeginInvokeSafe(() => _link = text);

    private void UpdateStatus()
    {
        var link = _link;
        if (_streaming && _pipeline.Fps > 0)
            link = $"● Connecté à {_connected?.Name} · {_pipeline.SourceWidth}×{_pipeline.SourceHeight} · {_pipeline.Fps} ips";
        else if (_connectCts == null)
            link = "Non connecté";
        _linkLabel.Text = link;

        if (!DriverInstaller.IsInstalled)
            _camLabel.Text = "Webcam virtuelle non installée (menu ⋮)";
        else if (!_pipeline.CameraActive)
            _camLabel.Text = "Webcam virtuelle : démarrage…";
        else
            _camLabel.Text = _pipeline.CameraInUse
                ? "« CamTel Camera » utilisée par une appli"
                : "« CamTel Camera » prête";
        _tray.Text = _streaming ? $"CamTel · {_pipeline.Fps} ips" : "CamTel";
    }

    private void OnPreview(Bitmap frame)
    {
        if (!Visible || WindowState == FormWindowState.Minimized || IsDisposed)
        {
            frame.Dispose();
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                var old = _previewImage;
                _previewImage = frame;
                _preview.Image = frame;
                old?.Dispose();
            });
        }
        catch (InvalidOperationException)
        {
            frame.Dispose();
        }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(a);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("camtel.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }
}
