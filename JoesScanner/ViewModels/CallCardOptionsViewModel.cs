using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public sealed class CallCardOptionsViewModel : BindableObject
{
    private readonly CallCardOptionsService _options;
    private CallCardLayout _draft = CallCardLayout.Default;
    private CallCardFieldOption _selectedField = CallCardLayout.Default.Fields[0];
    private bool _loaded;
    private bool _saving;
    private string _status = string.Empty;

    public CallCardOptionsViewModel(CallCardOptionsService options)
    {
        _options = options;
        MoveUpCommand = new Command(() => Edit(Draft.Move(SelectedField.Field, -1)),
            () => CanEdit && Draft.GetPosition(SelectedField.Field).Index > 0);
        MoveDownCommand = new Command(() => Edit(Draft.Move(SelectedField.Field, 1)), () =>
        {
            var (index, count) = Draft.GetPosition(SelectedField.Field);
            return CanEdit && index + 1 < count;
        });
        MoveSectionCommand = new Command(() =>
        {
            var section = IsInHeader ? CallCardSection.Secondary : CallCardSection.Header;
            Edit(Draft.MoveToSection(SelectedField.Field, section));
        }, () => CanEdit);
        ResetCommand = new Command(() => Edit(CallCardLayout.Default), () => CanEdit);
        ApplyCommand = new Command(async () => await ApplyAsync(),
            () => CanEdit && !Draft.HasSameOptions(_options.Layout));
    }

    public System.Collections.IList Fields { get; } = CallCardLayout.Default.Fields.ToArray();
    public CallCardLayout Draft => _draft;
    public bool CanEdit => _loaded && !_saving;
    public string Status => _status;
    public Command MoveUpCommand { get; }
    public Command MoveDownCommand { get; }
    public Command MoveSectionCommand { get; }
    public Command ResetCommand { get; }
    public Command ApplyCommand { get; }

    public CallCardFieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            if (value == null || _selectedField == value)
                return;

            _selectedField = value;
            OnPropertyChanged();
            RefreshField();
        }
    }

    public bool ShowSelectedField
    {
        get => Draft.GetOption(SelectedField.Field).Enabled;
        set
        {
            if (CanEdit)
                Edit(Draft.SetEnabled(SelectedField.Field, value));
        }
    }

    public bool ShowDuration
    {
        get => Draft.ShowDuration;
        set
        {
            if (CanEdit)
                Edit(Draft.SetShowDuration(value));
        }
    }

    public bool ShowDurationPlacementHint => ShowDuration && SelectedField.Field == CallCardField.Duration;

    private bool IsInHeader => Draft.GetOption(SelectedField.Field).Section == CallCardSection.Header;
    public string MoveSectionText => IsInHeader ? "Move to Secondary Details" : "Move to Header";

    public string PositionText
    {
        get
        {
            var (index, count) = Draft.GetPosition(SelectedField.Field);
            var section = IsInHeader ? "Header" : "Secondary Details";
            return $"{section} · {index + 1} of {count}";
        }
    }

    public CallCardMetadata Preview { get; } = new()
    {
        Timestamp = new DateTime(2026, 1, 1, 14, 32, 5),
        System = "County P25",
        Talkgroup = "Fire Dispatch",
        Site = "North Tower",
        Radio = "Engine 12",
        Receiver = "Receiver 1",
        DurationSeconds = 8.4,
        Frequency = "851.1250",
        TalkgroupTag = "Fire Dispatch",
        RadioTag = "Fire",
        SystemType = "P25",
        CallType = "Group",
        VoiceType = "Digital",
        Lcn = "4"
    };

    internal async Task LoadAsync()
    {
        await _options.Ready;
        if (_loaded)
            return;

        _loaded = true;
        _draft = _options.Layout;
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(CanEdit));
        RefreshField();
    }

    private void Edit(CallCardLayout draft)
    {
        if (ReferenceEquals(draft, _draft))
            return;

        _draft = draft;
        SetStatus(string.Empty);
        OnPropertyChanged(nameof(Draft));
        RefreshField();
    }

    private async Task ApplyAsync()
    {
        if (!CanEdit)
            return;

        _saving = true;
        OnPropertyChanged(nameof(CanEdit));
        RefreshField();
        try
        {
            await _options.ApplyAsync(Draft);
            SetStatus("Applied to Live and History.");
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Call card options: could not save layout ({ex.GetType().Name}).");
            SetStatus("Could not save call card options. Try Apply again.");
        }
        finally
        {
            _saving = false;
            OnPropertyChanged(nameof(CanEdit));
            RefreshField();
        }
    }

    private void SetStatus(string text)
    {
        if (_status == text)
            return;

        _status = text;
        OnPropertyChanged(nameof(Status));
    }

    private void RefreshField()
    {
        OnPropertyChanged(nameof(ShowSelectedField));
        OnPropertyChanged(nameof(ShowDuration));
        OnPropertyChanged(nameof(ShowDurationPlacementHint));
        OnPropertyChanged(nameof(MoveSectionText));
        OnPropertyChanged(nameof(PositionText));
        MoveUpCommand.ChangeCanExecute();
        MoveDownCommand.ChangeCanExecute();
        MoveSectionCommand.ChangeCanExecute();
        ResetCommand.ChangeCanExecute();
        ApplyCommand.ChangeCanExecute();
    }
}
