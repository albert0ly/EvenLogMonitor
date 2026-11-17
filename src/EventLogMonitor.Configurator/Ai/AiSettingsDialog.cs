using System.Drawing;
using System.Windows.Forms;

namespace EventLogMonitor.Configurator.Ai;

/// <summary>Small dialog to set the Ollama endpoint/model and test the connection.</summary>
public sealed class AiSettingsDialog : Form
{
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 900, Width = 90 };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    public AiSettings Settings { get; }

    public AiSettingsDialog(AiSettings settings)
    {
        Settings = settings;
        Text = "AI settings (local model)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 230);
        Font = new Font("Segoe UI", 9F);

        _endpoint.Text = settings.Endpoint;
        _model.Text = settings.Model;
        _timeout.Value = Math.Clamp(settings.TimeoutSeconds, 10, 900);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 120, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "Ollama endpoint:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_endpoint, 1, 0);
        grid.Controls.Add(new Label { Text = "Model:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        grid.Controls.Add(_model, 1, 1);
        grid.Controls.Add(new Label { Text = "Timeout (sec):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        grid.Controls.Add(_timeout, 1, 2);

        var test = new Button { Text = "Test connection", AutoSize = true };
        test.Click += async (_, _) => await TestAsync();
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Apply();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        buttons.Controls.AddRange([test, ok, cancel]);

        var info = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        info.Controls.Add(_result);
        Controls.Add(info);
        Controls.Add(buttons);
        Controls.Add(grid);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void Apply()
    {
        Settings.Endpoint = _endpoint.Text.Trim();
        Settings.Model = _model.Text.Trim();
        Settings.TimeoutSeconds = (int)_timeout.Value;
        try { Settings.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Could not save AI settings: " + ex.Message, "AI settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task TestAsync()
    {
        _result.ForeColor = Color.DarkSlateGray;
        _result.Text = "Testing…";
        try
        {
            var probe = new AiSettings { Endpoint = _endpoint.Text.Trim(), Model = _model.Text.Trim(), TimeoutSeconds = 15 };
            using var client = new OllamaClient(probe);
            var problem = await client.CheckAsync();
            _result.ForeColor = problem is null ? Color.DarkGreen : Color.Firebrick;
            _result.Text = problem ?? "Connected. The model is installed.";
        }
        catch (Exception ex) { _result.ForeColor = Color.Firebrick; _result.Text = ex.Message; }
    }
}
