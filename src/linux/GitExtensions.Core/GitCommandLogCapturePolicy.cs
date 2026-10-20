using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>Host-supplied diagnostics policy. A capture retains symbols, never process data or source locations.</summary>
public sealed class GitCommandLogCapturePolicy
{
    public const int MaxFrames = 32;
    public const int MaxFrameLength = 256;
    public const int MaxCallStackLength = 4096;
    private const string Truncated = "[truncated]";

    public GitCommandLogCapturePolicy(bool captureCallStacks, Func<bool?>? uiThreadProbe = null, Assembly? approvedHostAssembly = null)
    {
        if (approvedHostAssembly?.IsDynamic == true)
        { throw new ArgumentException("The approved host assembly must not be dynamic.", nameof(approvedHostAssembly)); }
        CaptureCallStacks = captureCallStacks;
        UiThreadProbe = uiThreadProbe;
        ApprovedHostAssembly = approvedHostAssembly;
    }

    public bool CaptureCallStacks { get; }
    public Func<bool?>? UiThreadProbe { get; }
    public Assembly? ApprovedHostAssembly { get; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal GitCommandLogLaunchContext Capture()
    {
        bool? isOnMainThread = null;
        try { isOnMainThread = UiThreadProbe?.Invoke(); }
        catch (Exception) { /* A diagnostic probe must not change Git or retain its fault. */ }
        if (!CaptureCallStacks) { return new(null, isOnMainThread); }
        string? callStack = null;
        try { callStack = Project(new StackTrace(skipFrames: 1, fNeedFileInfo: false)); }
        catch (Exception) { /* Never retain an exception or its text as a substitute for a stack. */ }
        return new(callStack, isOnMainThread);
    }

    // Also used by deterministic metadata tests. This never reads source locations,
    // parameter values, closed generic arguments, exception data or executable paths.
    internal string? Project(StackTrace trace)
    {
        try { return ProjectCore(trace); }
        catch (Exception) { return null; }
    }

    private string? ProjectCore(StackTrace trace)
    {
        StringBuilder text = new(MaxCallStackLength);
        int count = 0;
        bool truncated = false;
        // Reserve a final marker even when a single frame is unusually long.
        int contentLimit = MaxCallStackLength - Truncated.Length - 1;
        for (int index = 0; index < trace.FrameCount; index++)
        {
            MethodBase? method = trace.GetFrame(index)?.GetMethod();
            Type? type = method?.DeclaringType;
            if (type is null || type.Assembly.IsDynamic
                || !ReferenceEquals(type.Assembly, typeof(GitCommandLog).Assembly)
                    && !ReferenceEquals(type.Assembly, ApprovedHostAssembly)) { continue; }
            if (type == typeof(GitCommandLog) || type == typeof(GitCommandLogCapturePolicy)) { continue; }
            if (count == MaxFrames) { truncated = true; break; }
            if (type.IsGenericType) { type = type.GetGenericTypeDefinition(); }
            string typeName = type.FullName ?? type.Name;
            string methodName = method!.Name;
            StringBuilder line = new(MaxFrameLength);
            line.Append("at ");
            AppendName(line, typeName);
            if (line.Length < MaxFrameLength) { line.Append('.'); }
            AppendName(line, methodName);
            if ((long)typeName.Length + methodName.Length + 4 > MaxFrameLength)
            { line[MaxFrameLength - 1] = '…'; truncated = true; }
            int needed = line.Length + (text.Length == 0 ? 0 : 1);
            if (needed > contentLimit - text.Length) { truncated = true; break; }
            if (text.Length != 0) { text.Append('\n'); }
            text.Append(line);
            count++;
        }
        if (text.Length == 0) { return null; }
        if (truncated) { text.Append('\n').Append(Truncated); }
        return text.ToString();
    }

    private static void AppendName(StringBuilder target, string name)
    {
        int length = Math.Min(name.Length, MaxFrameLength - target.Length);
        for (int index = 0; index < length; index++)
        {
            char character = name[index];
            target.Append(char.IsControl(character) ? '?' : character);
        }
    }
}

internal readonly record struct GitCommandLogLaunchContext(string? CallStack, bool? IsOnMainThread);
