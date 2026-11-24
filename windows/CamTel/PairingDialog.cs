namespace CamTel;

/// <summary>Asks for the 6-digit "Code PC" shown by the phone app (Wi-Fi pairing, once per phone).</summary>
public sealed class PairingDialog : Form
{
    private readonly TextBox _code;

    private PairingDialog(string phoneName, string? error)
    {
        Text = $"Appairer {phoneName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(400, error == null ? 196 : 236);

        var top = 18;
        var intro = new Label
        {
            AutoSize = false, Location = new Point(20, top), Size = new Size(360, 44), ForeColor = Theme.Muted,
            Text = "Entre le « Code PC » affiché en haut à gauche de l'app CamTel, sur le téléphone.",
        };
        Controls.Add(intro);
        top += 50;
        if (error != null)
        {
            Controls.Add(new Label
            {
                AutoSize = false, Location = new Point(20, top), Size = new Size(360, 40),
                Font = new Font("Segoe UI Semibold", 10f), Text = error,
            });
            top += 40;
        }
        _code = new TextBox
        {
            Location = new Point(110, top), Width = 180, MaxLength = 7, TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 18f), BackColor = Theme.Panel, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "123 456",
        };
        _code.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != ' ') e.Handled = true;
        };
        Controls.Add(_code);
        top += 62;

        var ok = new Button
        {
            Text = "Appairer", DialogResult = DialogResult.OK, Location = new Point(196, top), Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(292, top), Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Text,
        };
        cancel.FlatAppearance.BorderColor = Theme.Hover;
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(Handle);
    }

    /// <summary>The 6 digits typed, or null if the user cancelled.</summary>
    public static string? Ask(IWin32Window owner, string phoneName, string? error)
    {
        using var dialog = new PairingDialog(phoneName, error);
        while (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            var digits = new string(dialog._code.Text.Where(char.IsDigit).ToArray());
            if (digits.Length == 6) return digits;
            dialog._code.SelectAll();
        }
        return null;
    }
}
