namespace GitExtensions.Linux.Desktop;

public sealed record CommitMessageTemplate(string Name = "", string Text = "", bool IsRegex = false);

/// <summary>Application templates and the six FormCommitTemplateSettings options. Git commit.template is independent.</summary>
public sealed record CommitMessagePreferences
{
    public IReadOnlyList<CommitMessageTemplate> Templates { get; init; } = Array.AsReadOnly(Enumerable.Range(0, 10).Select(_ => new CommitMessageTemplate()).ToArray());
    public int MaxFirstLineLength { get; init; }
    public int MaxLineLength { get; init; }
    public bool AutoWrap { get; init; } = true;
    public string ValidationRegex { get; init; } = "";
    public bool IndentAfterFirstLine { get; init; } = true;
    public bool SecondLineMustBeEmpty { get; init; }

    public CommitMessagePreferences WithTemplate(int index, CommitMessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        CommitMessageTemplate[] templates = Templates.ToArray(); templates[index] = template;
        return this with { Templates = Array.AsReadOnly(templates) };
    }
    internal CommitMessagePreferences Snapshot()
    {
        Validate();
        return this with { Templates = Array.AsReadOnly(Templates.Concat(Enumerable.Range(0, Math.Max(0, 10 - Templates.Count)).Select(_ => new CommitMessageTemplate())).ToArray()) };
    }
    internal void Validate()
    {
        if (MaxFirstLineLength < 0 || MaxLineLength < 0) { throw new ArgumentOutOfRangeException(nameof(MaxLineLength)); }
        ArgumentNullException.ThrowIfNull(ValidationRegex); ArgumentNullException.ThrowIfNull(Templates);
        foreach (CommitMessageTemplate template in Templates)
        { ArgumentNullException.ThrowIfNull(template); ArgumentNullException.ThrowIfNull(template.Name); ArgumentNullException.ThrowIfNull(template.Text); }
    }
    internal bool SameAs(CommitMessagePreferences other) => MaxFirstLineLength == other.MaxFirstLineLength && MaxLineLength == other.MaxLineLength
        && AutoWrap == other.AutoWrap && ValidationRegex == other.ValidationRegex && IndentAfterFirstLine == other.IndentAfterFirstLine
        && SecondLineMustBeEmpty == other.SecondLineMustBeEmpty && Templates.SequenceEqual(other.Templates);
}
