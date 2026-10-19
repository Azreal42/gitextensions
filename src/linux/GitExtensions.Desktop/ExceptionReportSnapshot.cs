using System.Collections;
using System.Reflection;
using System.Text;

namespace GitExtensions.Linux.Desktop;

internal sealed record ExceptionReportProperty(string Name, string Value, bool Extended = false);
internal sealed record ExceptionReportNode(string Type, string Message, string TargetSite,
    IReadOnlyList<ExceptionReportProperty> Properties, IReadOnlyList<ExceptionReportNode> Children);

/// <summary>Local immutable strings, captured once from the actual exception; arbitrary getters never escape.</summary>
internal sealed class ExceptionReportSnapshot
{
    internal const string Unavailable = "<Unable to read this value>";
    internal Exception OriginalException { get; }
    internal ExceptionReportNode Root { get; }
    internal string FullText { get; }
    private ExceptionReportSnapshot(Exception original, ExceptionReportNode root)
    { OriginalException = original; Root = root; FullText = Format(root); }

    internal static string MessageOf(Exception error) => ReadString(() => error.Message);

    internal static ExceptionReportSnapshot Capture(Exception original)
    {
        ArgumentNullException.ThrowIfNull(original);
        Dictionary<Exception, Builder> builders = new(ReferenceEqualityComparer.Instance);
        Queue<Exception> pending = new(); pending.Enqueue(original);
        while (pending.TryDequeue(out Exception? exception))
        {
            if (builders.ContainsKey(exception)) { continue; }
            Builder builder = Read(exception); builders.Add(exception, builder);
            foreach (Exception child in builder.Inner) { if (!builders.ContainsKey(child)) { pending.Enqueue(child); } }
        }
        // Iterative postorder avoids stack overflow for long chains and explicitly marks only cyclic edges.
        Dictionary<Exception, ExceptionReportNode> frozen = new(ReferenceEqualityComparer.Instance);
        HashSet<Exception> active = new(ReferenceEqualityComparer.Instance);
        Stack<(Exception Error, int Index, List<ExceptionReportNode> Children)> stack = new();
        stack.Push((original, 0, [])); active.Add(original);
        while (stack.Count > 0)
        {
            var frame = stack.Pop(); Builder builder = builders[frame.Error];
            if (frame.Index < builder.Inner.Count)
            {
                Exception child = builder.Inner[frame.Index];
                if (frozen.TryGetValue(child, out ExceptionReportNode? known))
                { frame.Children.Add(known); stack.Push((frame.Error, frame.Index + 1, frame.Children)); }
                else if (active.Contains(child))
                {
                    frame.Children.Add(new(ReadString(() => child.GetType().FullName), "<Cyclic exception reference>", "", [], []));
                    stack.Push((frame.Error, frame.Index + 1, frame.Children));
                }
                else
                { stack.Push(frame); active.Add(child); stack.Push((child, 0, [])); }
                continue;
            }
            frozen.Add(frame.Error, new(builder.Type, builder.Message, builder.Target,
                Array.AsReadOnly(builder.Properties.ToArray()), Array.AsReadOnly(frame.Children.ToArray())));
            active.Remove(frame.Error);
        }
        return new(original, frozen[original]);
    }

    private sealed record Builder(string Type, string Message, string Target,
        List<ExceptionReportProperty> Properties, IReadOnlyList<Exception> Inner);

    private static Builder Read(Exception exception)
    {
        string type = ReadString(() => exception.GetType().FullName);
        string message = ReadString(() => exception.Message);
        string target = ReadString(() => exception.TargetSite is { } method ? method + " @ " + method.DeclaringType : null);
        List<Exception> children = [];
        if (exception is AggregateException aggregate)
        {
            // Aggregate.InnerException is already the first list entry. Keep the actual list positions,
            // including a repeated reference supplied twice; do not add the first entry a second time.
            foreach (Exception child in aggregate.InnerExceptions) { children.Add(child); }
        }
        else if (exception.InnerException is { } inner) { children.Add(inner); }
        List<ExceptionReportProperty> properties = [new("Exception", type), new("Message", message)];
        Add("Target Site", target);
        if (exception.InnerException is { } first) { Add("Inner Exception", ReadString(() => first.GetType().FullName)); }
        Add("Source", ReadString(() => exception.Source)); Add("Help Link", ReadString(() => exception.HelpLink));
        Add("Stack Trace", ReadString(() => exception.StackTrace));
        try
        {
            foreach (DictionaryEntry entry in exception.Data)
            { properties.Add(new("Data[\"" + ReadString(() => entry.Key) + "\"]", ReadValue(() => entry.Value))); }
        }
        catch (Exception) { properties.Add(new("Data", Unavailable)); }
        string[] builtIn = ["Data", "InnerExceptions", "InnerException", "Message", "Source", "StackTrace", "TargetSite", "HelpLink"];
        try
        {
            foreach (PropertyInfo property in exception.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || builtIn.Contains(property.Name, StringComparer.Ordinal)) { continue; }
                properties.Add(new(property.Name, ReadValue(() => property.GetValue(exception)), true));
            }
        }
        catch (Exception) { properties.Add(new("Extended Properties", Unavailable, true)); }
        if (exception is GitCommandException git)
        {
            properties.Add(new("Command", "git " + string.Join(' ', git.Arguments), true));
            properties.Add(new("Exit Code", git.Result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), true));
            properties.Add(new("Standard Output", git.Result.StandardOutput, true));
            properties.Add(new("Standard Error", git.Result.StandardError, true));
        }
        return new(type, message, target, properties, Array.AsReadOnly(children.ToArray()));
        void Add(string name, string value) { if (value.Length > 0) { properties.Add(new(name, value)); } }
    }

    private static string ReadString(Func<object?> read)
    { try { return read()?.ToString() ?? ""; } catch (Exception) { return Unavailable; } }

    private static string ReadValue(Func<object?> read)
    { try { object? value = read(); return value is null ? "<null>" : value.ToString() ?? ""; } catch (Exception) { return Unavailable; } }

    private static string Format(ExceptionReportNode root)
    {
        StringBuilder text = new(); Stack<(ExceptionReportNode Node, int Depth)> nodes = new(); nodes.Push((root, 0));
        while (nodes.TryPop(out var item))
        {
            string indent = new(' ', item.Depth * 2);
            text.Append(indent).Append(item.Node.Type).Append(": ").AppendLine(item.Node.Message);
            foreach (ExceptionReportProperty property in item.Node.Properties.Where(value => value.Name is not ("Exception" or "Message" or "Inner Exception")))
            { text.Append(indent).Append(property.Name).Append(": ").AppendLine(property.Value); }
            for (int index = item.Node.Children.Count - 1; index >= 0; index--) { nodes.Push((item.Node.Children[index], item.Depth + 1)); }
        }
        return text.ToString();
    }
}
