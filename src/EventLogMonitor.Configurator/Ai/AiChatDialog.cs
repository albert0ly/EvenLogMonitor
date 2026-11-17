using System.Drawing;
using System.Windows.Forms;

namespace EventLogMonitor.Configurator.Ai;

public sealed class AiChatDialog : Form
{
    private readonly RichTextBox _transcript = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, DetectUrls = false };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Describe what you want to monitor, or answer the AI's question…" };
    private readonly Button _send = new() { Text = "Send", Dock = DockStyle.Fill };
    private readonly Button _apply = new() { Text = "Apply draft", Dock = DockStyle.Fill, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DarkSlateGray };
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
        Size = new Size(820, 650);
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_transcript, 0, 0);
        root.Controls.Add(_input, 0, 1);
        root.Controls.Add(_status, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 4, 0, 4) };
        _send.AutoSize = true;
        _apply.AutoSize = true;
        _send.Dock = DockStyle.None;
        _apply.Dock = DockStyle.None;
        _send.MinimumSize = new Size(110, 36);
        _apply.MinimumSize = new Size(120, 36);
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_send);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        _send.Click += async (_, _) => await SendAsync();
        _apply.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };
        Shown += (_, _) => _input.Focus();
    }

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
        _status.Text = "  Asking the local model…";
        try
        {
            var outcome = await new RuleChatService(_settings).ReplyAsync(_conversation, _context, _knownLogNames);
            Append("AI", outcome.Text);
            _conversation.Add(new ChatTurn("assistant", outcome.Text));
            Outcome = outcome.Edit is not null || outcome.NewRules.Count > 0 ? outcome : null;
            _apply.Enabled = Outcome is not null;
            _apply.Text = outcome.Edit is not null ? "Apply edit" : "Apply draft";
            _status.Text = Outcome is null ? "  Ask another question or request an edit." :
                "  Review the proposed change, then click Apply. Save configuration afterward.";
        }
        catch (Exception ex)
        {
            Append("AI error", ex.Message);
            _status.Text = "  The request failed. You can send another message.";
        }
        finally
        {
            _sending = false;
            _send.Enabled = true;
            _input.Enabled = true;
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
