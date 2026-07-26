using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class SaveGameWindow : BaseWindow
{
    public event Action Closed;
    public event Action<string> CreateRequested;
    public event Action<string> OverwriteRequested;
    public event Action<string> RetrySyncRequested;
    public event Action<string> LoadRequested;

    private TextField _nameField;
    private VisualElement _list;
    private Label _status;
    private Button _createButton;
    private Button _overwriteButton;
    private Button _retryButton;
    private Button _loadButton;
    private Button _closeButton;
    private string _selectedSaveId;
    private bool _busy;

    public bool IsBusy => _busy;

    protected override void OnBindElements()
    {
        _nameField = RootElement.Q<TextField>("save-name");
        _list = RootElement.Q<VisualElement>("save-list");
        _status = RootElement.Q<Label>("save-status");
        _createButton = RootElement.Q<Button>("save-create");
        _overwriteButton = RootElement.Q<Button>("save-overwrite");
        _retryButton = RootElement.Q<Button>("save-retry");
        _loadButton = RootElement.Q<Button>("save-load");
        _closeButton = RootElement.Q<Button>("save-close");
    }

    protected override void OnOpen()
    {
        if (_createButton != null) _createButton.clicked += OnCreate;
        if (_overwriteButton != null) _overwriteButton.clicked += OnOverwrite;
        if (_retryButton != null) _retryButton.clicked += OnRetry;
        if (_loadButton != null) _loadButton.clicked += OnLoad;
        if (_closeButton != null) _closeButton.clicked += Close;
        UpdateButtons();
    }

    protected override void OnClose()
    {
        UnbindButtons();
        try { Closed?.Invoke(); }
        catch (Exception ex) { Debug.LogWarning($"SaveGameWindow: Closed callback failed: {ex.Message}"); }
    }

    public override void OnDestroy()
    {
        UnbindButtons();
        base.OnDestroy();
    }

    public void SetEntries(IReadOnlyList<SaveGameSummary> entries)
    {
        _list?.Clear();
        _selectedSaveId = null;
        if (_list == null) return;
        if (entries == null || entries.Count == 0)
        {
            var empty = new Label("还没有存档");
            empty.AddToClassList("save-empty");
            _list.Add(empty);
            UpdateButtons();
            return;
        }
        foreach (SaveGameSummary entry in entries)
        {
            var row = new Button(() => Select(entry.SaveId));
            row.AddToClassList("save-row");
            string sync = entry.ConversationSynced ? "对话已同步" : "对话未同步";
            row.text = $"{entry.DisplayName}\n{entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {sync}";
            row.userData = entry.SaveId;
            _list.Add(row);
        }
        UpdateButtons();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        _nameField?.SetEnabled(!busy);
        UpdateButtons();
    }

    public void SetStatus(string message, bool isError = false)
    {
        if (_status == null) return;
        _status.text = message ?? string.Empty;
        _status.EnableInClassList("save-status--error", isError);
    }

    private void Select(string saveId)
    {
        _selectedSaveId = saveId;
        if (_list != null)
        {
            foreach (VisualElement child in _list.Children())
                child.EnableInClassList("save-row--selected", string.Equals(child.userData as string, saveId, StringComparison.Ordinal));
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasSelection = !_busy && !string.IsNullOrWhiteSpace(_selectedSaveId);
        _createButton?.SetEnabled(!_busy);
        _overwriteButton?.SetEnabled(hasSelection);
        _retryButton?.SetEnabled(hasSelection);
        _loadButton?.SetEnabled(hasSelection);
        _closeButton?.SetEnabled(!_busy);
    }

    private void OnCreate() => CreateRequested?.Invoke(_nameField?.value);
    private void OnOverwrite() { if (!string.IsNullOrWhiteSpace(_selectedSaveId)) OverwriteRequested?.Invoke(_selectedSaveId); }
    private void OnRetry() { if (!string.IsNullOrWhiteSpace(_selectedSaveId)) RetrySyncRequested?.Invoke(_selectedSaveId); }
    private void OnLoad() { if (!string.IsNullOrWhiteSpace(_selectedSaveId)) LoadRequested?.Invoke(_selectedSaveId); }

    private void UnbindButtons()
    {
        if (_createButton != null) _createButton.clicked -= OnCreate;
        if (_overwriteButton != null) _overwriteButton.clicked -= OnOverwrite;
        if (_retryButton != null) _retryButton.clicked -= OnRetry;
        if (_loadButton != null) _loadButton.clicked -= OnLoad;
        if (_closeButton != null) _closeButton.clicked -= Close;
    }
}