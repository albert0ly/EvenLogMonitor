using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EventLogMonitor;
using EventLogMonitor.Configurator.Ai;

namespace EventLogMonitor.Configurator;

public sealed class MainForm : Form
{
    private readonly TextBox _configPath = new() { Dock = DockStyle.Fill };
    private readonly ListBox _rulesList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _ruleName = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _enabled = new() { Text = "Enabled", Checked = true, AutoSize = true };
    private readonly ComboBox _logName = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _providers = CreateCheckedList();
    private readonly CheckedListBox _eventIds = CreateCheckedList();
    private readonly CheckedListBox _levels = CreateCheckedList();
    private readonly CheckedListBox _appPools = CreateCheckedList();
    private readonly TextBox _extraIds = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g. 1000, 1026, 1309" };
    private readonly TextBox _messageContains = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional phrase in the event message" };
    private readonly TextBox _manualPool = new() { Width = 160, PlaceholderText = "Pool name" };
    private readonly TextBox _manualProvider = new() { Width = 190, PlaceholderText = "Provider / source name" };
    private readonly NumericUpDown _cooldown = new() { Minimum = 0, Maximum = 86400, Value = 60, Width = 110 };
    private readonly CheckBox _notifyEmail = new() { Text = "Email", Checked = true, AutoSize = true };
    private readonly CheckBox _notifySms = new() { Text = "SMS", AutoSize = true };
    private readonly ListView _preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DarkSlateGray };
    private readonly Button _loadEvents = new() { Text = "Load recent events", AutoSize = true };
    private readonly Button _previewMatches = new() { Text = "Preview matches", AutoSize = true };
    private readonly Button _saveRule = new() { Text = "Save rule", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _saveConfiguration = new() { Text = "Save configuration", AutoSize = true, Enabled = false };
    private readonly TextBox _aiRequest = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "e.g. I have ASP.NET application 'Claims' in local IIS. Watch critical errors and notify me when only an IIS reset can help."
    };
    private readonly Button _draftButton = new() { Text = "Open AI chat", Dock = DockStyle.Fill };
    private readonly Button _aiSettingsButton = new() { Text = "AI settings…", Dock = DockStyle.Fill };
    private readonly AiSettings _aiSettings = AiSettings.Load();

    private readonly List<EventRule> _rules = [];
    private List<SampleEvent> _sampleEvents = [];
    private int _selectedRuleIndex = -1;
    private string? _initialConfigPath;
    private bool _loadingEditor;
    private bool _configurationDirty;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public MainForm(string? configPath)
    {
        _initialConfigPath = configPath;
        Text = "Event Log Monitor – Rule Builder";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 760);
        Size = new Size(1450, 980);
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        FormClosing += ConfirmUnsavedChanges;
        _rulesList.SelectedIndexChanged += (_, _) => SelectRule(_rulesList.SelectedIndex);
        _logName.SelectedIndexChanged += (_, _) => ClearEventChoices();
        _loadEvents.Click += (_, _) => LoadRecentEvents();
        _previewMatches.Click += (_, _) => PreviewCurrentRule();
        _draftButton.Click += (_, _) => DraftWithAi();
        _aiSettingsButton.Click += (_, _) => OpenAiSettings();
        _ruleName.TextChanged += (_, _) => UpdateSaveRuleState();
        _enabled.CheckedChanged += (_, _) => UpdateSaveRuleState();
        _logName.SelectedIndexChanged += (_, _) => UpdateSaveRuleState();
        _extraIds.TextChanged += (_, _) => UpdateSaveRuleState();
        _messageContains.TextChanged += (_, _) => UpdateSaveRuleState();
        _cooldown.ValueChanged += (_, _) => UpdateSaveRuleState();
        _notifyEmail.CheckedChanged += (_, _) => UpdateSaveRuleState();
        _notifySms.CheckedChanged += (_, _) => UpdateSaveRuleState();
        foreach (var list in new[] { _providers, _eventIds, _levels, _appPools })
            list.ItemCheck += (_, _) => BeginInvoke((Action)UpdateSaveRuleState);
        LoadLogNames();
        LoadIisPools();
        LoadConfiguration(ResolveInitialConfigPath());
    }

    private static CheckedListBox CreateCheckedList() => new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
        HorizontalScrollbar = true
    };

    private string ResolveInitialConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_initialConfigPath)) return Path.GetFullPath(_initialConfigPath);
        var local = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(local)) return local;
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "appsettings.json")))
            return Path.Combine(parent, "appsettings.json");
        return local;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var pathBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pathBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        pathBar.Controls.Add(new Label { Text = "Configuration:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        pathBar.Controls.Add(_configPath, 1, 0);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => BrowseConfiguration();
        pathBar.Controls.Add(browse, 2, 0);
        var reload = new Button { Text = "Reload", Dock = DockStyle.Fill };
        reload.Click += (_, _) => LoadConfiguration(_configPath.Text);
        pathBar.Controls.Add(reload, 3, 0);
        root.Controls.Add(pathBar, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 255, FixedPanel = FixedPanel.Panel1 };
        root.Controls.Add(split, 0, 1);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 4, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        left.Controls.Add(new Label { Text = "Notification rules", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        left.Controls.Add(_rulesList, 0, 1);
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var newRule = new Button { Text = "+ New rule", Width = 220 };
        newRule.Click += (_, _) => NewRule();
        var deleteRule = new Button { Text = "Delete selected", Width = 220 };
        deleteRule.Click += (_, _) => DeleteSelectedRule();
        var addIisRules = new Button { Text = "Add IIS/.NET templates", Width = 220 };
        addIisRules.Click += (_, _) => AddIisTemplates();
        leftButtons.Controls.Add(newRule);
        leftButtons.Controls.Add(deleteRule);
        leftButtons.Controls.Add(addIisRules);
        left.Controls.Add(leftButtons, 0, 2);
        split.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8, 4, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        split.Panel2.Controls.Add(right);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        header.Controls.Add(FieldLabel("Rule name:"), 0, 0);
        header.Controls.Add(_ruleName, 1, 0);
        header.Controls.Add(_enabled, 2, 0);
        header.SetColumnSpan(_enabled, 2);
        header.Controls.Add(FieldLabel("Event log:"), 0, 1);
        header.Controls.Add(_logName, 1, 1);
        header.Controls.Add(_loadEvents, 2, 1);
        header.SetColumnSpan(_loadEvents, 2);
        var aiPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        aiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        aiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        aiPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        aiPanel.Controls.Add(_aiRequest, 0, 0);
        aiPanel.Controls.Add(_draftButton, 1, 0);
        aiPanel.Controls.Add(_aiSettingsButton, 2, 0);
        right.Controls.Add(MakeGroup("Ask AI about notification rules or request a change (review before saving)", aiPanel), 0, 0);
        right.Controls.Add(header, 0, 1);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 4, 0, 4) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var providerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        providerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        providerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        providerPanel.Controls.Add(_providers, 0, 0);
        var addProvider = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        addProvider.Controls.Add(_manualProvider);
        var addProviderButton = new Button { Text = "Add", Width = 58, Height = 25 };
        addProviderButton.Click += (_, _) => AddManualProvider();
        addProvider.Controls.Add(addProviderButton);
        providerPanel.Controls.Add(addProvider, 0, 1);
        filters.Controls.Add(MakeGroup("Event sources / providers (nothing checked = any)", providerPanel), 0, 0);

        var eventIdPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        eventIdPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        eventIdPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        eventIdPanel.Controls.Add(_eventIds, 0, 0);
        eventIdPanel.Controls.Add(_extraIds, 0, 1);
        filters.Controls.Add(MakeGroup("Event IDs (nothing checked = any)", eventIdPanel), 1, 0);
        filters.Controls.Add(MakeGroup("Event levels (nothing checked = any)", _levels), 0, 1);

        var poolPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        poolPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        poolPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        poolPanel.Controls.Add(_appPools, 0, 0);
        var addPool = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        addPool.Controls.Add(_manualPool);
        var addPoolButton = new Button { Text = "Add", Width = 58, Height = 25 };
        addPoolButton.Click += (_, _) => AddManualPool();
        addPool.Controls.Add(addPoolButton);
        var refreshPools = new Button { Text = "Refresh", Width = 68, Height = 25 };
        refreshPools.Click += (_, _) => LoadIisPools();
        addPool.Controls.Add(refreshPools);
        poolPanel.Controls.Add(addPool, 0, 1);
        filters.Controls.Add(MakeGroup("IIS application pools (nothing checked = any)", poolPanel), 1, 1);
        right.Controls.Add(filters, 0, 2);

        var messageAndOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Padding = new Padding(0, 2, 0, 2) };
        messageAndOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        messageAndOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        messageAndOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        messageAndOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        messageAndOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        messageAndOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        messageAndOptions.Controls.Add(FieldLabel("Message contains:"), 0, 0);
        messageAndOptions.Controls.Add(_messageContains, 1, 0);
        messageAndOptions.SetColumnSpan(_messageContains, 3);
        messageAndOptions.Controls.Add(FieldLabel("Cooldown (sec):"), 0, 1);
        messageAndOptions.Controls.Add(_cooldown, 1, 1);
        var channelPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        channelPanel.Controls.Add(_notifyEmail);
        channelPanel.Controls.Add(_notifySms);
        messageAndOptions.Controls.Add(channelPanel, 2, 1);
        _saveRule.Click += (_, _) => SaveCurrentRule();
        messageAndOptions.Controls.Add(_saveRule, 3, 1);
        right.Controls.Add(messageAndOptions, 0, 3);

        var actionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _saveConfiguration.Click += (_, _) => SaveConfiguration();
        actionBar.Controls.Add(_saveConfiguration);
        actionBar.Controls.Add(_previewMatches);
        actionBar.Controls.Add(_status);
        right.Controls.Add(actionBar, 0, 4);

        _preview.Columns.Add("Time", 145);
        _preview.Columns.Add("Provider", 210);
        _preview.Columns.Add("Event ID", 70);
        _preview.Columns.Add("Level", 90);
        _preview.Columns.Add("Application pool", 170);
        _preview.Columns.Add("PID", 75);
        _preview.Columns.Add("Message", 500);
        right.Controls.Add(MakeGroup("Preview of recent matching events", _preview), 0, 5);
    }

    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private static GroupBox MakeGroup(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(7) };
        group.Controls.Add(content);
        content.Dock = DockStyle.Fill;
        return group;
    }

    private void BrowseConfiguration()
    {
        using var dialog = new OpenFileDialog { Filter = "Configuration (appsettings.json)|appsettings.json|JSON files (*.json)|*.json|All files (*.*)|*.*", FileName = "appsettings.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _configPath.Text = dialog.FileName;
    }

    private void LoadLogNames()
    {
        try
        {
            var names = EventLogSession.GlobalSession.GetLogNames()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            _logName.Items.AddRange(names);
            if (_logName.Items.Count == 0) _logName.Items.Add("Application");
            var standard = _logName.Items.IndexOf("Application");
            _logName.SelectedIndex = standard >= 0 ? standard : 0;
        }
        catch (Exception ex)
        {
            _logName.Items.Add("Application");
            _logName.Items.Add("System");
            _logName.Items.Add("Security");
            _logName.SelectedIndex = 0;
            SetStatus("Could not enumerate event channels: " + ex.Message);
        }
    }

    private void LoadIisPools()
    {
        var previous = _appPools.Items.Cast<FilterChoice>()
            .Select((choice, index) => (choice, index))
            .Where(x => _appPools.GetItemChecked(x.index))
            .Select(x => x.choice.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = IisApplicationPoolCatalog.Read();
        var names = snapshot.ApplicationPools.Concat(previous)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        _appPools.BeginUpdate();
        _appPools.Items.Clear();
        foreach (var pool in names)
            _appPools.Items.Add(new FilterChoice(pool, pool), previous.Contains(pool));
        _appPools.EndUpdate();
        UpdateSaveRuleState();
        SetStatus(snapshot.Error is null
            ? $"Detected {snapshot.ApplicationPools.Count} IIS application pool(s)."
            : "IIS pools could not be enumerated: " + snapshot.Error + " You can add a pool name manually.");
    }

    private void LoadConfiguration(string path)
    {
        if (HasUnsavedChanges() && !ConfirmDiscardChanges()) return;
        try
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Configuration file not found. Use Browse to select the service appsettings.json.", path);
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("The JSON root must be an object.");
            var section = root["EventLogMonitor"]?.Deserialize<EventLogMonitorOptions>(JsonOptions)
                ?? new EventLogMonitorOptions();
            _rules.Clear();
            _rules.AddRange(section.Rules ?? []);
            _configPath.Text = path;
            RefreshRuleList(-1);
            if (_rules.Count > 0) _rulesList.SelectedIndex = 0;
            else NewRule();
            _configurationDirty = false;
            UpdateSaveConfigurationState();
            SetStatus($"Loaded {_rules.Count} rule(s). Save applies changes to this file; restart the Event Log Monitor service afterward.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshRuleList(int selectedIndex)
    {
        _rulesList.BeginUpdate();
        _rulesList.Items.Clear();
        foreach (var rule in _rules)
            _rulesList.Items.Add((rule.Enabled ? "● " : "○ ") + rule.Name + "  [" + rule.LogName + "]");
        _rulesList.EndUpdate();
        if (selectedIndex >= 0 && selectedIndex < _rulesList.Items.Count) _rulesList.SelectedIndex = selectedIndex;
    }

    private void SelectRule(int index)
    {
        if (_loadingEditor || index < 0 || index >= _rules.Count) return;
        _selectedRuleIndex = index;
        LoadEditor(_rules[index]);
    }

    private void LoadEditor(EventRule rule)
    {
        _loadingEditor = true;
        try
        {
            _ruleName.Text = rule.Name;
            _enabled.Checked = rule.Enabled;
            SelectLog(rule.LogName);
            SetCheckedValues(_providers, rule.Providers);
            SetCheckedValues(_eventIds, rule.EventIds.Select(x => x.ToString()).ToList());
            SetCheckedValues(_levels, rule.Levels);
            SetCheckedValues(_appPools, rule.ApplicationPools);
            _extraIds.Text = "";
            _messageContains.Text = rule.MessageContains;
            _cooldown.Value = Math.Clamp(rule.CooldownSeconds, (int)_cooldown.Minimum, (int)_cooldown.Maximum);
            _notifyEmail.Checked = rule.NotifyEmail;
            _notifySms.Checked = rule.NotifySms;
        }
        finally { _loadingEditor = false; }
        UpdateSaveRuleState();
    }

    private void SelectLog(string logName)
    {
        var index = _logName.Items.IndexOf(logName);
        if (index < 0)
        {
            _logName.Items.Add(logName);
            index = _logName.Items.Count - 1;
        }
        _logName.SelectedIndex = index;
    }

    private static void SetCheckedValues(CheckedListBox list, IEnumerable<string> values)
    {
        var wanted = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Items.Count; i++)
        {
            var choice = (FilterChoice)list.Items[i];
            list.SetItemChecked(i, wanted.Contains(choice.Value));
        }
        foreach (var value in wanted)
        {
            if (!list.Items.Cast<FilterChoice>().Any(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new FilterChoice(value, value), true);
        }
    }

    private static bool EditorMatchesRule(EventRule editor, EventRule rule) =>
        editor.Name == rule.Name && editor.Enabled == rule.Enabled && editor.LogName == rule.LogName &&
        editor.EventIds.SequenceEqual(rule.EventIds.Order()) &&
        editor.Providers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(rule.Providers) &&
        editor.Levels.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(rule.Levels) &&
        editor.ApplicationPools.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(rule.ApplicationPools) &&
        editor.MessageContains == rule.MessageContains && editor.CooldownSeconds == rule.CooldownSeconds &&
        editor.NotifyEmail == rule.NotifyEmail && editor.NotifySms == rule.NotifySms;

    private void NewRule()
    {
        _selectedRuleIndex = -1;
        _rulesList.ClearSelected();
        _loadingEditor = true;
        try
        {
            _ruleName.Text = "New rule";
            _enabled.Checked = true;
            SelectLog("Application");
            ClearChecks(_providers, _eventIds, _levels, _appPools);
            _extraIds.Clear();
            _messageContains.Clear();
            _cooldown.Value = 60;
            _notifyEmail.Checked = true;
            _notifySms.Checked = false;
        }
        finally { _loadingEditor = false; }
        UpdateSaveRuleState();
        _preview.Items.Clear();
    }

    private static void ClearChecks(params CheckedListBox[] lists)
    {
        foreach (var list in lists)
            for (var i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false);
    }

    private void AddIisTemplates()
    {
        var templates = new[]
        {
            new EventRule
            {
                Name = "IIS – WAS application pool failures", Enabled = true, LogName = "System",
                EventIds = [5002, 5011, 5059], Levels = ["Error", "Warning"],
                CooldownSeconds = 60, NotifyEmail = true, NotifySms = false
            },
            new EventRule
            {
                Name = "ASP.NET application errors", Enabled = true, LogName = "Application",
                EventIds = [1309], Levels = ["Error", "Warning"],
                CooldownSeconds = 60, NotifyEmail = true, NotifySms = false
            },
            new EventRule
            {
                Name = ".NET Runtime unhandled exceptions", Enabled = true, LogName = "Application",
                EventIds = [1026], Providers = [".NET Runtime"], Levels = ["Error"],
                CooldownSeconds = 60, NotifyEmail = true, NotifySms = false
            },
            new EventRule
            {
                Name = "IIS worker process crashes (w3wp.exe)", Enabled = true, LogName = "Application",
                EventIds = [1000, 1001], MessageContains = "w3wp.exe",
                CooldownSeconds = 60, NotifyEmail = true, NotifySms = false
            },
            new EventRule
            {
                Name = "ASP.NET Core Module errors", Enabled = true, LogName = "Application",
                Providers = ["IIS AspNetCore Module", "IIS AspNetCore Module V2", "IIS Express AspNetCore Module"],
                Levels = ["Error", "Warning"], CooldownSeconds = 60, NotifyEmail = true, NotifySms = false
            }
        };

        var added = 0;
        foreach (var template in templates)
        {
            if (_rules.Any(x => string.Equals(x.Name, template.Name, StringComparison.OrdinalIgnoreCase))) continue;
            _rules.Add(template);
            added++;
        }
        RefreshRuleList(added > 0 ? _rules.Count - 1 : _selectedRuleIndex);
        if (added > 0) _rulesList.SelectedIndex = _rules.Count - added;
        if (added > 0) _configurationDirty = true;
        UpdateSaveRuleState();
        UpdateSaveConfigurationState();
        SetStatus($"Added {added} IIS/.NET starter rule(s). Review them and click Save configuration.");
    }

    private void DeleteSelectedRule()
    {
        if (_selectedRuleIndex < 0 || _selectedRuleIndex >= _rules.Count) return;
        var index = _selectedRuleIndex;
        _rules.RemoveAt(index);
        _configurationDirty = true;
        UpdateSaveConfigurationState();
        _selectedRuleIndex = -1;
        RefreshRuleList(Math.Min(index, _rules.Count - 1));
        if (_rulesList.SelectedIndex < 0) NewRule();
    }

    private void SaveCurrentRule()
    {
        try
        {
            var rule = ReadEditor();
            if (_selectedRuleIndex >= 0 && _selectedRuleIndex < _rules.Count)
                _rules[_selectedRuleIndex] = rule;
            else
            {
                _rules.Add(rule);
                _selectedRuleIndex = _rules.Count - 1;
            }
            _configurationDirty = true;
            RefreshRuleList(_selectedRuleIndex);
            UpdateSaveRuleState();
            UpdateSaveConfigurationState();
            SetStatus("Rule saved in editor. Click Save configuration to write appsettings.json.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid rule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateSaveConfigurationState()
    {
        _saveConfiguration.Enabled = _configurationDirty;
    }

    private void UpdateSaveRuleState()
    {
        if (_loadingEditor) return;
        if (string.IsNullOrWhiteSpace(_ruleName.Text) || _logName.SelectedItem is null)
        {
            _saveRule.Enabled = false;
            return;
        }

        try
        {
            var current = ReadEditor();
            _saveRule.Enabled = _selectedRuleIndex < 0 || _selectedRuleIndex >= _rules.Count ||
                !EditorMatchesRule(current, _rules[_selectedRuleIndex]);
        }
        catch (Exception)
        {
            _saveRule.Enabled = false;
        }
    }

    private EventRule ReadEditor()
    {
        var name = _ruleName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a rule name.");
        if (_logName.SelectedItem is null) throw new InvalidOperationException("Select an event log.");

        var eventIds = CheckedValues(_eventIds).Select(v => int.Parse(v)).ToHashSet();
        foreach (Match match in Regex.Matches(_extraIds.Text, @"\d+"))
            if (int.TryParse(match.Value, out var id)) eventIds.Add(id);

        return new EventRule
        {
            Name = name,
            Enabled = _enabled.Checked,
            LogName = _logName.SelectedItem.ToString()!,
            EventIds = eventIds.Order().ToList(),
            Providers = CheckedValues(_providers),
            Levels = CheckedValues(_levels),
            ApplicationPools = CheckedValues(_appPools),
            MessageContains = _messageContains.Text.Trim(),
            // The basic UI does not expose regex editing; preserve an existing advanced rule.
            MessageRegex = _selectedRuleIndex >= 0 && _selectedRuleIndex < _rules.Count
                ? _rules[_selectedRuleIndex].MessageRegex : "",
            CooldownSeconds = (int)_cooldown.Value,
            NotifyEmail = _notifyEmail.Checked,
            NotifySms = _notifySms.Checked
        };
    }

    private static List<string> CheckedValues(CheckedListBox list) => list.CheckedItems
        .Cast<FilterChoice>().Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void SaveConfiguration()
    {
        try
        {
            var current = ReadEditor();
            if (_selectedRuleIndex >= 0 && _selectedRuleIndex < _rules.Count)
                _rules[_selectedRuleIndex] = current;
            else
            {
                _rules.Add(current);
                _selectedRuleIndex = _rules.Count - 1;
            }
            RefreshRuleList(_selectedRuleIndex);
            UpdateSaveRuleState();

            var path = Path.GetFullPath(_configPath.Text.Trim());
            JsonObject root;
            if (File.Exists(path))
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            else
                root = new JsonObject();

            var monitor = root["EventLogMonitor"] as JsonObject;
            if (monitor is null)
            {
                monitor = new JsonObject();
                root["EventLogMonitor"] = monitor;
            }
            var logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (monitor["Logs"] is JsonArray existingLogs)
                foreach (var item in existingLogs)
                    if (!string.IsNullOrWhiteSpace(item?.GetValue<string>())) logs.Add(item!.GetValue<string>());
            foreach (var rule in _rules)
                if (!string.IsNullOrWhiteSpace(rule.LogName) && rule.LogName != "*") logs.Add(rule.LogName);
            if (logs.Count == 0) { logs.Add("Application"); logs.Add("System"); }

            monitor["Logs"] = new JsonArray(logs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            monitor["Rules"] = JsonSerializer.SerializeToNode(_rules, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(JsonOptions));
            _configPath.Text = path;
            _configurationDirty = false;
            UpdateSaveConfigurationState();
            SetStatus("Configuration saved. Restart the Event Log Monitor Windows service to apply the new rules.");
            MessageBox.Show(this, "Configuration saved successfully.\r\n\r\nRestart the Event Log Monitor service to apply the changes.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadRecentEvents()
    {
        if (_logName.SelectedItem is null) return;
        var log = _logName.SelectedItem.ToString()!;
        try
        {
            UseWaitCursor = true;
            _sampleEvents = ReadRecentEvents(log, 600);
            PopulateChoices(_providers, _sampleEvents.GroupBy(x => x.Provider).Select(g => new FilterChoice(g.Key, $"{g.Key} ({g.Count()})")), preserveChecks: true);
            PopulateChoices(_eventIds, _sampleEvents.GroupBy(x => x.EventId).OrderBy(g => g.Key)
                .Select(g => new FilterChoice(g.Key.ToString(), $"{g.Key} ({g.Count()})")), preserveChecks: true);
            PopulateChoices(_levels, _sampleEvents.Select(x => x.Level).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new FilterChoice(x!, x!)), preserveChecks: true);
            UpdateSaveRuleState();

            _preview.Items.Clear();
            foreach (var item in _sampleEvents.Take(30)) AddPreviewRow(item);
            SetStatus($"Loaded {_sampleEvents.Count} recent events from {log}. Select providers, IDs and levels, then Preview matches.");
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"Access denied reading '{log}'. Run the configurator as Administrator or grant access to this event log.\r\n\r\n{ex.Message}",
                "Event log read failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (EventLogException ex)
        {
            MessageBox.Show(this, $"Could not read event log '{log}'. Verify that the channel exists and is enabled.\r\n\r\n{ex}",
                "Event log read failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load recent events from '{log}'.\r\n\r\n{ex}",
                "Event log read failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor = false; }
    }

    private static List<SampleEvent> ReadRecentEvents(string logName, int maxCount)
    {
        var list = new List<SampleEvent>();
        var query = new EventLogQuery(logName, PathType.LogName, "*") { ReverseDirection = true, TolerateQueryErrors = true };
        using var reader = new EventLogReader(query);
        var poolSnapshot = IisApplicationPoolCatalog.Read();
        for (var i = 0; i < maxCount; i++)
        {
            using var record = reader.ReadEvent();
            if (record is null) break;
            string xml;
            try { xml = record.ToXml(); } catch { xml = ""; }
            string message;
            try { message = record.FormatDescription() ?? ""; } catch { message = xml; }
            var pid = ReadProcessId(xml);
            var pools = ReadPoolNames(xml, message).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (pid is not null && poolSnapshot.WorkerProcesses.TryGetValue(pid.Value, out var mapped)) pools.Add(mapped);
            string level;
            try { level = record.LevelDisplayName ?? LevelName(record.Level); }
            catch (EventLogException) { level = LevelName(record.Level); }
            list.Add(new SampleEvent(logName, record.TimeCreated ?? DateTime.Now, record.ProviderName ?? "Unknown", record.Id,
                level, message, pid, pools.ToArray()));
        }
        return list;
    }

    private static int? ReadProcessId(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            // Prefer event payload data (such as FaultingProcessId) over the event writer's PID.
            foreach (var element in doc.Descendants())
            {
                var fieldName = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? element.Name.LocalName;
                if (!fieldName.Contains("processid", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ParseProcessId(element.Value);
                if (parsed is not null) return parsed;
            }
            var exec = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Execution");
            var text = exec?.Attributes().FirstOrDefault(x => x.Name.LocalName == "ProcessID")?.Value;
            return ParseProcessId(text);
        }
        catch { return null; }
    }

    private static int? ParseProcessId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (int.TryParse(candidate, out var pid) && pid > 0) return pid;
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) candidate = candidate[2..];
        return int.TryParse(candidate, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out pid) && pid > 0 ? pid : null;
    }

    private static IEnumerable<string> ReadPoolNames(string xml, string message)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var element in doc.Descendants())
            {
                var name = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? element.Name.LocalName;
                var isPoolName = name.Contains("poolname", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("pool", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("apppool", StringComparison.OrdinalIgnoreCase);
                if (isPoolName && !string.IsNullOrWhiteSpace(element.Value))
                    found.Add(element.Value.Trim());
            }
        }
        catch { }
        var patterns = new[]
        {
            """\bapplication\s+pool\s+['"“](?<pool>[^'"”\r\n]+)['"”]""",
            """\bapplication\s+pool\s+(?<pool>[A-Za-z0-9_.()$ -]+?)\s+(?:has|is|was|could|cannot|failed|reached|requested|suffered|encountered|terminated|stopped|disabled)\b"""
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["pool"].Value))
                found.Add(match.Groups["pool"].Value.Trim().TrimEnd('.', ',', ';', ':'));
        }
        return found;
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "Critical", 2 => "Error", 3 => "Warning", 4 => "Information", 5 => "Verbose", _ => "Unknown"
    };

    private static void PopulateChoices(CheckedListBox target, IEnumerable<FilterChoice> items, bool preserveChecks)
    {
        HashSet<string> checkedValues = preserveChecks
            ? CheckedValues(target).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var available = items.ToList();
        var availableValues = available.Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in checkedValues.Where(x => !availableValues.Contains(x)))
            available.Add(new FilterChoice(value, value));
        target.BeginUpdate();
        target.Items.Clear();
        foreach (var item in available.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
            target.Items.Add(item, checkedValues.Contains(item.Value));
        target.EndUpdate();
    }

    private void ClearEventChoices()
    {
        if (_loadingEditor) return;
        _sampleEvents.Clear();
        PopulateChoices(_providers, [], preserveChecks: false);
        PopulateChoices(_eventIds, [], preserveChecks: false);
        PopulateChoices(_levels, [], preserveChecks: false);
        _preview.Items.Clear();
        UpdateSaveRuleState();
    }

    private void PreviewCurrentRule()
    {
        try
        {
            var rule = ReadEditor();
            _preview.Items.Clear();
            var matches = _sampleEvents.Where(x => MatchesPreview(rule, x)).Take(100).ToList();
            foreach (var item in matches) AddPreviewRow(item);
            SetStatus($"{matches.Count} matching event(s) shown from {_sampleEvents.Count} recently loaded event(s). This is a preview, not a live test.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot preview", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static bool MatchesPreview(EventRule rule, SampleEvent item)
    {
        if (!string.IsNullOrWhiteSpace(rule.LogName) && rule.LogName != "*" &&
            !string.Equals(rule.LogName, item.LogName, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.EventIds.Count > 0 && !rule.EventIds.Contains(item.EventId)) return false;
        if (rule.Providers.Count > 0 && !rule.Providers.Contains(item.Provider, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.Levels.Count > 0 && !rule.Levels.Contains(item.Level, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.ApplicationPools.Count > 0 && !rule.ApplicationPools.Any(p => item.ApplicationPools.Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(rule.MessageContains) && !item.Message.Contains(rule.MessageContains, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void AddPreviewRow(SampleEvent item)
    {
        var row = new ListViewItem(item.Time.ToString("yyyy-MM-dd HH:mm:ss"));
        row.SubItems.Add(item.Provider);
        row.SubItems.Add(item.EventId.ToString());
        row.SubItems.Add(item.Level);
        row.SubItems.Add(item.ApplicationPools.Count == 0 ? "(unknown)" : string.Join(", ", item.ApplicationPools));
        row.SubItems.Add(item.ProcessId?.ToString() ?? "");
        row.SubItems.Add(item.Message.Replace("\r", " ").Replace("\n", " "));
        row.Tag = item;
        _preview.Items.Add(row);
    }

    private void AddManualProvider()
    {
        var name = _manualProvider.Text.Trim();
        if (name.Length == 0) return;
        var existing = _providers.Items.Cast<FilterChoice>()
            .Select((choice, index) => (choice, index))
            .FirstOrDefault(x => string.Equals(x.choice.Value, name, StringComparison.OrdinalIgnoreCase));
        if (existing.choice is not null)
            _providers.SetItemChecked(existing.index, true);
        else
            _providers.Items.Add(new FilterChoice(name, name), true);
        _manualProvider.Clear();
        UpdateSaveRuleState();
    }

    private void AddManualPool()
    {
        var name = _manualPool.Text.Trim();
        if (name.Length == 0) return;
        if (!_appPools.Items.Cast<FilterChoice>().Any(x => string.Equals(x.Value, name, StringComparison.OrdinalIgnoreCase)))
            _appPools.Items.Add(new FilterChoice(name, name), true);
        _manualPool.Clear();
        UpdateSaveRuleState();
    }

    private void OpenAiSettings()
    {
        using var dialog = new AiSettingsDialog(_aiSettings);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetStatus($"AI settings saved. Model: {_aiSettings.Model} at {_aiSettings.Endpoint}.");
    }

    private void DraftWithAi()
    {
        var knownLogs = _logName.Items.Cast<object>().Select(x => x.ToString() ?? "").Where(x => x.Length > 0).ToList();
        var context = new RuleChatContext(_rules.Select(RuleChatContext.Copy).ToList(), _rulesList.SelectedIndex);
        using var chat = new AiChatDialog(_aiSettings, context, knownLogs, _aiRequest.Text);
        if (chat.ShowDialog(this) != DialogResult.OK || chat.Outcome is null) return;

        var outcome = chat.Outcome;
        if (outcome.Edit is not null)
        {
            var index = outcome.Edit.RuleIndex;
            if (index < 0 || index >= _rules.Count || !RulesEquivalent(_rules[index], context.Rules[index]))
            {
                SetStatus("The target rule changed while the chat was open. Reopen AI chat before applying this edit.");
                return;
            }
            _rules[index] = outcome.Edit.Rule;
            _configurationDirty = true;
            RefreshRuleList(index);
            SelectRule(index);
            UpdateSaveRuleState();
            UpdateSaveConfigurationState();
            SetStatus($"Updated '{outcome.Edit.Rule.Name}' in memory. Review it, then click Save configuration.");
        }
        else
        {
            var firstNew = _rules.Count;
            _rules.AddRange(outcome.NewRules);
            if (outcome.NewRules.Count > 0) _configurationDirty = true;
            RefreshRuleList(firstNew);
            if (outcome.NewRules.Count > 0) SelectRule(firstNew);
            UpdateSaveRuleState();
            UpdateSaveConfigurationState();
            SetStatus($"Drafted {outcome.NewRules.Count} rule(s). Review them, then click Save configuration.");
        }
    }

    private static bool RulesEquivalent(EventRule a, EventRule b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private bool HasUnsavedChanges() => _configurationDirty || _saveRule.Enabled;

    private bool ConfirmDiscardChanges() => MessageBox.Show(this,
        "There are unsaved rule changes. Discard them? Click Cancel to return and use Save configuration.",
        "Unsaved rules", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;

    private void ConfirmUnsavedChanges(object? sender, FormClosingEventArgs e)
    {
        if (HasUnsavedChanges() && !ConfirmDiscardChanges()) e.Cancel = true;
    }

    private void SetStatus(string text) => _status.Text = "  " + text;

    private sealed record FilterChoice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record SampleEvent(string LogName, DateTime Time, string Provider, int EventId, string Level,
        string Message, int? ProcessId, IReadOnlyList<string> ApplicationPools);
}
