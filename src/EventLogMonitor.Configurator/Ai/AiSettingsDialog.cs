using System.Drawing;
using System.Windows.Forms;

namespace EventLogMonitor.Configurator.Ai;

/// <summary>Dialog to set the Ollama endpoint/model and test the connection.</summary>
public sealed class AiSettingsDialog : Form
{
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 900, Width = 96 };
    private readonly Label _result = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkSlateGray, AutoEllipsis = true };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true, MinimumSize = new Size(130, 36) };
    private readonly Button _save = new() { Text = "Save", MinimumSize = new Size(90, 36) };
    private bool _testing;
    public AiSettings Settings { get; }

    public AiSettingsDialog(AiSettings settings)
    {
        Settings = settings;
        Text = "AI settings · local model";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(580, 350);
        Size = new Size(650, 390);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        _endpoint.Text = settings.Endpoint;
        _model.Text = settings.Model;
        _timeout.Value = Math.Clamp(settings.TimeoutSeconds, 10, 900);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(new Label { Text = "Connect to a local Ollama model. Test the connection before saving if needed.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 14) }, 0, 0);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 3, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.Controls.Add(new Label { Text = "Ollama endpoint", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        grid.Controls.Add(_endpoint, 1, 0);
        grid.Controls.Add(new Label { Text = "Model name", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        grid.Controls.Add(_model, 1, 1);
        grid.Controls.Add(new Label { Text = "Timeout (seconds)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        grid.Controls.Add(_timeout, 1, 2);
        root.Controls.Add(grid, 0, 1);

        var feedback = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        feedback.Controls.Add(_test, 0, 0);
        _result.Text = "Connection not tested.";
        _result.Margin = new Padding(12, 0, 0, 0);
        feedback.Controls.Add(_result, 1, 0);
        root.Controls.Add(feedback, 0, 2);

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, MinimumSize = new Size(90, 36) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
        AcceptButton = _save;
        CancelButton = cancel;

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => Apply();
        _endpoint.TextChanged += (_, _) => ResetTestResult();
        _model.TextChanged += (_, _) => ResetTestResult();
    }

    private void ResetTestResult()
    {
        if (_testing) return;
        _result.ForeColor = Color.DarkSlateGray;
        _result.Text = "Connection not tested for these values.";
    }

    private void Apply()
    {
        var endpoint = _endpoint.Text.Trim();
        var model = _model.Text.Trim();
        var timeout = (int)_timeout.Value;
        var previous = (Settings.Endpoint, Settings.Model, Settings.TimeoutSeconds);
        Settings.Endpoint = endpoint;
        Settings.Model = model;
        Settings.TimeoutSeconds = timeout;
        try
        {
            Settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            (Settings.Endpoint, Settings.Model, Settings.TimeoutSeconds) = previous;
            MessageBox.Show(this, "Could not save AI settings: " + ex.Message, "AI settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task TestAsync()
    {
        if (_testing) return;
        _testing = true;
        _test.Enabled = false;
        _save.Enabled = false;
        _endpoint.Enabled = false;
        _model.Enabled = false;
        _timeout.Enabled = false;
        _result.ForeColor = Color.DarkSlateGray;
        _result.Text = "Testing connection…";
        try
        {
            var probe = new AiSettings { Endpoint = _endpoint.Text.Trim(), Model = _model.Text.Trim(), TimeoutSeconds = 15 };
            using var client = new OllamaClient(probe);
            var problem = await client.CheckAsync();
            _result.ForeColor = problem is null ? Color.DarkGreen : Color.Firebrick;
            _result.Text = problem ?? "Connected. The model is installed.";
        }
        catch (Exception ex) { _result.ForeColor = Color.Firebrick; _result.Text = ex.Message; }
        finally
        {
            _testing = false;
            _test.Enabled = true;
            _save.Enabled = true;
            _endpoint.Enabled = true;
            _model.Enabled = true;
            _timeout.Enabled = true;
        }
    }
}
