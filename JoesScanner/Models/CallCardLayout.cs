namespace JoesScanner.Models;

public enum CallCardField
{
    Time, System, Talkgroup, Site, Radio, Receiver, Duration, Frequency,
    TalkgroupTag, RadioTag, SystemType, CallType, VoiceType, Lcn
}

public enum CallCardSection { Header, Secondary }

public sealed record CallCardFieldOption(CallCardField Field, CallCardSection Section, bool Enabled)
{
    public string Name => Field switch
    {
        CallCardField.TalkgroupTag => "Talkgroup Tag",
        CallCardField.RadioTag => "Radio Tag",
        CallCardField.SystemType => "System Type",
        CallCardField.CallType => "Call Type",
        CallCardField.VoiceType => "Voice Type",
        CallCardField.Lcn => "LCN",
        _ => Field.ToString()
    };
}

public sealed class CallCardLayout
{
    public static CallCardLayout Default { get; } = new(Enum.GetValues<CallCardField>().Select(field => field switch
    {
        CallCardField.Time or CallCardField.System or CallCardField.Talkgroup => new(field, CallCardSection.Header, true),
        CallCardField.Site or CallCardField.Radio => new(field, CallCardSection.Secondary, true),
        _ => new CallCardFieldOption(field, CallCardSection.Secondary, false)
    }));

    private readonly CallCardFieldOption[] _fields;
    private readonly CallCardFieldOption[] _header;
    private readonly CallCardFieldOption[] _secondary;

    private CallCardLayout(IEnumerable<CallCardFieldOption> fields, bool showDuration = true)
    {
        ShowDuration = showDuration;
        _fields = fields.OrderBy(field => field.Section).ToArray();
        Fields = Array.AsReadOnly(_fields);
        var displayed = _fields.Where(field => field.Enabled && (!showDuration || field.Field != CallCardField.Duration));
        _header = displayed.Where(field => field.Section == CallCardSection.Header).ToArray();
        _secondary = displayed.Where(field => field.Section == CallCardSection.Secondary).ToArray();
    }

    public IReadOnlyList<CallCardFieldOption> Fields { get; }
    public bool ShowDuration { get; }

    public bool HasSameOptions(CallCardLayout other) =>
        ShowDuration == other.ShowDuration && Fields.SequenceEqual(other.Fields);

    public CallCardLayout SetShowDuration(bool show) => ShowDuration == show ? this : new(_fields, show);

    public string? GetCornerDuration(CallCardMetadata? metadata, bool compact) =>
        ShowDuration ? CallCardMetadata.FormatDuration(metadata?.DurationSeconds, compact) : null;

    public CallCardFieldOption GetOption(CallCardField field) => _fields.First(option => option.Field == field);

    public CallCardLayout SetEnabled(CallCardField field, bool enabled)
    {
        if (GetOption(field).Enabled == enabled)
            return this;

        return new(_fields.Select(option => option.Field == field ? option with { Enabled = enabled } : option), ShowDuration);
    }

    public CallCardLayout MoveToSection(CallCardField field, CallCardSection section)
    {
        var option = GetOption(field);
        return option.Section == section
            ? this
            : new(_fields.Where(item => item.Field != field).Append(option with { Section = section }), ShowDuration);
    }

    public (int Index, int Count) GetPosition(CallCardField field)
    {
        var section = GetOption(field).Section;
        var sectionFields = Array.FindAll(_fields, item => item.Section == section);
        return (Array.FindIndex(sectionFields, item => item.Field == field), sectionFields.Length);
    }

    public CallCardLayout Move(CallCardField field, int direction)
    {
        var index = Array.FindIndex(_fields, option => option.Field == field);
        var next = index + Math.Sign(direction);
        if (next == index || next < 0 || next >= _fields.Length || _fields[next].Section != _fields[index].Section)
            return this;

        var reordered = (CallCardFieldOption[])_fields.Clone();
        (reordered[index], reordered[next]) = (reordered[next], reordered[index]);
        return new(reordered, ShowDuration);
    }

    public IEnumerable<(CallCardField Field, string Text)> GetDisplayFields(CallCardMetadata metadata, CallCardSection section)
    {
        foreach (var option in section == CallCardSection.Header ? _header : _secondary)
        {
            if (metadata.GetValue(option.Field) is not { } value || string.IsNullOrWhiteSpace(value))
                continue;

            var text = section == CallCardSection.Header ? value.Trim() : $"{option.Name}: {value.Trim()}";
            yield return (option.Field, text);
        }
    }

    // Stable field and section names allow removed fields to be ignored and new fields to use their defaults.
    internal string Serialize() =>
        $"1;ShowDuration,{ShowDuration};" +
        string.Join(';', _fields.Select(option => $"{option.Field},{option.Section},{option.Enabled}"));

    internal static CallCardLayout Deserialize(string? saved)
    {
        if (saved == null || !saved.StartsWith("1;", StringComparison.Ordinal) || saved.Length > 4096)
            return Default;

        var fields = new List<CallCardFieldOption>();
        var seen = new HashSet<CallCardField>();
        bool? showDuration = null;
        foreach (var entry in saved[2..].Split(';'))
        {
            var parts = entry.Split(',');
            if (parts.Length == 2 && parts[0] == "ShowDuration" && bool.TryParse(parts[1], out var show))
                showDuration ??= show;

            if (parts.Length == 3 &&
                Enum.TryParse<CallCardField>(parts[0], out var field) && Enum.GetName(field) == parts[0] &&
                Enum.TryParse<CallCardSection>(parts[1], out var section) && Enum.GetName(section) == parts[1] &&
                bool.TryParse(parts[2], out var enabled) && seen.Add(field))
            {
                fields.Add(new(field, section, enabled));
            }
        }

        if (fields.Count == 0 && showDuration == null)
            return Default;

        fields.AddRange(Default.Fields.Where(option => !seen.Contains(option.Field)));
        return new(fields, showDuration ?? Default.ShowDuration);
    }
}
