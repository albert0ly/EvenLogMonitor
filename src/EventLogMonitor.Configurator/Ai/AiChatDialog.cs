using System.Drawing;
using System.Windows.Forms;

namespace EventLogMonitor.Configurator.Ai;

public sealed class AiChatDialog : Form
{
    private readonly RichTextBox _transcript = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, DetectUrls = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Describe what you want to monitor, or answer the AI's question…" };
    private readonly Button _send = new() { Text = "Send message", MinimumSize = new Size(120, 36) };
    private readonly Button _apply = new() { Text = "Apply draft", MinimumSize = new Size(120, 36), Enabled = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkSlateGray, AutoEllipsis = true };
    private readonly AiSettings _settings;
    private readonly RuleChatContext _context;
    private readonly IReadOnlyCollection<string> _knownLogNames;
    private readonly List<ChatTurn> _conversation = [];
    private bool _sending;

    public RuleChatReply? Outcome { get; private set; }

    public AiChatDialog(AiSettings settings, RuleChatContext context, IReadOnlyCollection<string> knownLogNames, string initialText)
    {
        _settings = settings;
        _context = context;
        _knownLogNames = knownLogNames;
        _input.Text = initialText;
        Text = "AI rule assistant";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 520);
        Size = new Size(860, 680);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        root.Controls.Add(new Label { Text = "Draft a monitoring rule", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        root.Controls.Add(new Label { Text = "Describe your goal, review the assistant's reply, then apply a proposed change when ready.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 12) }, 0, 1);
        root.Controls.Add(_transcript, 0, 2);
        root.Controls.Add(new Label { Text = "Your message  ·  Enter to send, Shift+Enter for a new line", AutoSize = true, Margin = new Padding(0, 12, 0, 5) }, 0, 3);
        root.Controls.Add(_input, 0, 4);
        _status.Text = "Enter a message to get started.";
        root.Controls.Add(_status, 0, 5);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_send);
        root.Controls.Add(buttons, 0, 6);
        Controls.Add(root);
        AcceptButton = _send;

        _send.Click += async (_, _) => await SendAsync();
        _apply.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _input.TextChanged += (_, _) => UpdateSendState();
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };
        UpdateSendState();
        Shown += (_, _) => _input.Focus();
    }

    private void UpdateSendState() => _send.Enabled = !_sending && !string.IsNullOrWhiteSpace(_input.Text);

    private async Task SendAsync()
    {
        if (_sending) return;
        var text = _input.Text.Trim();
        if (text.Length == 0) return;

        _sending = true;
        Outcome = null;
        _apply.Enabled = false;
        _send.Enabled = false;
        _input.Enabled = false;
        Append("You", text);
        _conversation.Add(new ChatTurn("user", text));
        _input.Clear();
        _status.Text = "Asking the local model…";
        try
        {
            var outcome = await new RuleChatService(_settings).ReplyAsync(_conversation, _context, _knownLogNames);
            Append("AI", outcome.Text);
            _conversation.Add(new ChatTurn("assistant", outcome.Text));
            Outcome = outcome.Edit is not null || outcome.NewRules.Count > 0 ? outcome : null;
            _apply.Enabled = Outcome is not null;
            _apply.Text = outcome.Edit is not null ? "Apply edit" : "Apply draft";
            _status.Text = Outcome is null ? "Ask another question or request an edit." :
                "Review the proposed change, then click Apply. Save configuration afterward.";
        }
        catch (Exception ex)
        {
            Append("AI error", ex.Message);
            _status.Text = "The request failed. You can send another message.";
        }
        finally
        {
            _sending = false;
            _input.Enabled = true;
            UpdateSendState();
            _input.Focus();
        }
    }

    private void Append(string speaker, string text)
    {
        _transcript.SelectionColor = speaker == "You" ? Color.DarkBlue : Color.DarkGreen;
        _transcript.SelectionFont = new Font(_transcript.Font, FontStyle.Bold);
        _transcript.AppendText(speaker + ":\r\n");
        _transcript.SelectionColor = Color.Black;
        _transcript.SelectionFont = new Font(_transcript.Font, FontStyle.Regular);
        _transcript.AppendText(text.Trim() + "\r\n\r\n");
        _transcript.ScrollToCaret();
    }
}
