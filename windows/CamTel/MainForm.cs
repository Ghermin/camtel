using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CamTel;

public sealed class MainForm : Form
{
    private const int PhonePort = 4747;
    private const int UsbLocalPort = 4747;
    private const string RepoUrl = "https://github.com/Ghermin/camtel";

    private static readonly (string Label, int W, int H)[] OutputSizes =
    {
        ("640 × 480", 640, 480), ("1280 × 720 (HD)", 1280, 720), ("1920 × 1080 (Full HD)", 1920, 1080),
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
    private bool _updatingUi;
    private Image? _previewImage;
    private bool _reallyClose;

    // UI
    private readonly ComboBox _phoneBox = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
    private readonly Button _connectBtn = new() { Text = "Connecter", AutoSize = true };
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom,
    };
    private readonly Button _cameraBtn = new() { Text = "Caméra : arrière", AutoSize = true };
    private readonly CheckBox _torchBox = new() { Text = "Flash", AutoSize = true };
    private readonly ComboBox _phoneResBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly TrackBar _qualityBar = new() { Minimum = 30, Maximum = 100, Value = 80, TickFrequency = 10, Width = 180 };
    private readonly TrackBar _zoomBar = new() { Minimum = 0, Maximum = 100, Value = 0, TickFrequency = 10, Width = 180 };
    private readonly ComboBox _outputBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly ComboBox _rotationBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly CheckBox _mirrorBox = new() { Text = "Miroir", AutoSize = true };
    private readonly CheckBox _fillBox = new() { Text = "Remplir (rogner)", AutoSize = true };
    private readonly CheckBox _autoBox = new() { Text = "Connexion auto au démarrage", AutoSize = true };
    private readonly ToolStripStatusLabel _linkLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _camLabel = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _usbTimer = new() { Interval = 3000 };

    public MainForm()
    {
        Text = "CamTel";
        Icon = LoadIcon();
        MinimumSize = new Size(900, 560);
        Size = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _pipeline = new FramePipeline(_settings.OutputWidth, _settings.OutputHeight)
        {
            Rotation = _settings.Rotation,
            Mirror = _settings.Mirror,
            Fill = _settings.Fill,
        };
        _pipeline.Preview += OnPreview;

        BuildUi();
        WireEvents();

        _discovery.Changed += () => BeginInvokeSafe(RefreshPhoneList);
        _discovery.Start();
        _statusTimer.Start();
        _usbTimer.Start();
    }

    // ---- Layout -------------------------------------------------------------

    private void BuildUi()
    {
        var menu = new MenuStrip();
        var camMenu = new ToolStripMenuItem("Webcam virtuelle");
        camMenu.DropDownItems.Add("Installer / réparer", null, (_, _) => InstallDriver(force: true));
        camMenu.DropDownItems.Add("Désinstaller", null, (_, _) => UninstallDriver());
        var helpMenu = new ToolStripMenuItem("Aide");
        helpMenu.DropDownItems.Add("Page GitHub", null, (_, _) => OpenUrl(RepoUrl));
        helpMenu.DropDownItems.Add("Télécharger l'app Android", null, (_, _) => OpenUrl(RepoUrl + "/releases/latest"));
        helpMenu.DropDownItems.Add("À propos", null, (_, _) => MessageBox.Show(this,
            $"CamTel {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}\n\n" +
            "Utilise ton téléphone Android comme webcam.\n" +
            "Caméra virtuelle basée sur softcam (MIT).\n\n" + RepoUrl, "À propos de CamTel"));
        menu.Items.Add(camMenu);
        menu.Items.Add(helpMenu);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), WrapContents = false,
        };
        top.Controls.Add(new Label { Text = "Téléphone :", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        top.Controls.Add(_phoneBox);
        top.Controls.Add(_connectBtn);
        top.Controls.Add(new Label
        {
            Text = "Choisis un téléphone détecté (Wi-Fi ou USB) ou tape son adresse IP.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(10, 7, 0, 0),
        });

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 260, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(10, 6, 10, 6),
        };
        side.Controls.Add(Header("Téléphone"));
        side.Controls.Add(_cameraBtn);
        side.Controls.Add(_torchBox);
        side.Controls.Add(Caption("Résolution du téléphone"));
        side.Controls.Add(_phoneResBox);
        side.Controls.Add(Caption("Qualité JPEG"));
        side.Controls.Add(_qualityBar);
        side.Controls.Add(Caption("Zoom"));
        side.Controls.Add(_zoomBar);
        side.Controls.Add(Header("Webcam PC (« CamTel Camera »)"));
        side.Controls.Add(Caption("Format de sortie"));
        side.Controls.Add(_outputBox);
        side.Controls.Add(Caption("Rotation"));
        side.Controls.Add(_rotationBox);
        side.Controls.Add(_mirrorBox);
        side.Controls.Add(_fillBox);
        side.Controls.Add(Header("Démarrage"));
        side.Controls.Add(_autoBox);

        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 0, 10) };
        previewHost.Controls.Add(_preview);

        var status = new StatusStrip();
        status.Items.Add(_linkLabel);
        status.Items.Add(_camLabel);

        Controls.Add(previewHost);
        Controls.Add(side);
        Controls.Add(top);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        _phoneResBox.Items.AddRange(PhoneResolutions);
        _phoneResBox.SelectedIndex = 1;
        foreach (var o in OutputSizes) _outputBox.Items.Add(o.Label);
        var outIdx = Array.FindIndex(OutputSizes, o => o.W == _settings.OutputWidth && o.H == _settings.OutputHeight);
        _outputBox.SelectedIndex = outIdx < 0 ? 1 : outIdx;
        _rotationBox.Items.AddRange(new object[] { "0°", "90°", "180°", "270°" });
        _rotationBox.SelectedIndex = ((_settings.Rotation / 90) % 4 + 4) % 4;
        _mirrorBox.Checked = _settings.Mirror;
        _fillBox.Checked = _settings.Fill;
        _autoBox.Checked = _settings.AutoConnect;
        if (_settings.LastHost.Length > 0 && !_settings.LastUsb) _phoneBox.Text = _settings.LastHost;
        SetPhoneControlsEnabled(false);

        _tray.Icon = Icon;
        _tray.Text = "CamTel";
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add("Afficher", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Quitter", null, (_, _) => { _reallyClose = true; Close(); });
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Label Header(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 12, 0, 6),
    };

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 2),
    };

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
        _cameraBtn.Click += (_, _) => SendControl("camera=toggle");
        _torchBox.CheckedChanged += (_, _) => { if (!_updatingUi) SendControl("torch=" + (_torchBox.Checked ? "on" : "off")); };
        _phoneResBox.SelectedIndexChanged += (_, _) => { if (!_updatingUi) SendControl("res=" + _phoneResBox.SelectedItem); };
        _qualityBar.MouseUp += (_, _) => SendControl("quality=" + _qualityBar.Value);
        _qualityBar.KeyUp += (_, _) => SendControl("quality=" + _qualityBar.Value);
        _zoomBar.ValueChanged += (_, _) =>
        {
            if (!_updatingUi) SendControl("zoom=" + (_zoomBar.Value / 100f).ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        _outputBox.SelectedIndexChanged += (_, _) =>
        {
            var o = OutputSizes[_outputBox.SelectedIndex];
            _settings.OutputWidth = o.W;
            _settings.OutputHeight = o.H;
            _pipeline.SetOutputSize(o.W, o.H);
            _settings.Save();
        };
        _rotationBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.Rotation = _rotationBox.SelectedIndex * 90;
            _pipeline.Rotation = _settings.Rotation;
            _settings.Save();
        };
        _mirrorBox.CheckedChanged += (_, _) => { _pipeline.Mirror = _settings.Mirror = _mirrorBox.Checked; _settings.Save(); };
        _fillBox.CheckedChanged += (_, _) => { _pipeline.Fill = _settings.Fill = _fillBox.Checked; _settings.Save(); };
        _autoBox.CheckedChanged += (_, _) => { _settings.AutoConnect = _autoBox.Checked; _settings.Save(); };
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
                "Une connexion est active. Quitter CamTel coupera la webcam.\n\nQuitter quand même ? (Non = réduire dans la barre des tâches)",
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
        _pipeline.SetOutputSize(_settings.OutputWidth, _settings.OutputHeight);
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
        SetPhoneControlsEnabled(false);
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
            BeginInvokeSafe(() => { ApplyPhoneStatus(status); SetPhoneControlsEnabled(true); });
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
            }
            if (ct.IsCancellationRequested) return;
            SetLink($"Connexion perdue avec {phone.Name}, nouvelle tentative…");
            _pipeline.Placeholder = "Connexion perdue, reconnexion…";
            if (!await DelayAsync(1500, ct)) return;
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
        var status = await MjpegClient.GetStringAsync(new Uri(api, "control?" + query));
        if (status != null)
        {
            // The phone applies changes asynchronously; read back the settled state.
            await Task.Delay(400);
            status = await MjpegClient.GetStringAsync(new Uri(api, "status")) ?? status;
            ApplyPhoneStatus(status);
        }
    }

    private void ApplyPhoneStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _updatingUi = true;
            _cameraBtn.Text = r.GetProperty("camera").GetString() == "front" ? "Caméra : avant" : "Caméra : arrière";
            _torchBox.Checked = r.GetProperty("torch").GetBoolean();
            var res = r.GetProperty("resolution").GetString();
            var idx = Array.IndexOf(PhoneResolutions, res);
            if (idx >= 0) _phoneResBox.SelectedIndex = idx;
            _qualityBar.Value = Math.Clamp(r.GetProperty("quality").GetInt32(), _qualityBar.Minimum, _qualityBar.Maximum);
            _zoomBar.Value = Math.Clamp((int)Math.Round(r.GetProperty("zoom").GetDouble() * 100), 0, 100);
        }
        catch
        {
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void SetPhoneControlsEnabled(bool enabled)
    {
        foreach (Control c in new Control[] { _cameraBtn, _torchBox, _phoneResBox, _qualityBar, _zoomBar })
            c.Enabled = enabled;
    }

    // ---- Status / preview ---------------------------------------------------

    private string _link = "Non connecté";

    private void SetLink(string text) => BeginInvokeSafe(() => _link = text);

    private void UpdateStatus()
    {
        var link = _link;
        if (_streaming && _pipeline.Fps > 0)
            link = $"Connecté à {_connected?.Name} · {_pipeline.SourceWidth}×{_pipeline.SourceHeight} · {_pipeline.Fps} ips";
        else if (_connectCts == null)
            link = "Non connecté";
        _linkLabel.Text = link;

        if (!DriverInstaller.IsInstalled)
            _camLabel.Text = "Webcam virtuelle non installée (menu Webcam virtuelle)";
        else if (!_pipeline.CameraActive)
            _camLabel.Text = "Webcam virtuelle : démarrage…";
        else
            _camLabel.Text = _pipeline.CameraInUse
                ? "« CamTel Camera » utilisée par une appli ●"
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
