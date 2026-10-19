using System.Collections;
using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class ExceptionReportProjectionTests
{
    [Test]
    public void ActualThrownChainRetainsStackTargetAndEveryInnerWithoutReadingOnlyTheRootMessage()
    {
        Exception error;
        try { ThrowNested(); throw new AssertionException("Must throw."); }
        catch (InvalidOperationException captured) { error = captured; }
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(error);
        Assert.That(snapshot.OriginalException, Is.SameAs(error));
        Assert.That(snapshot.Root.Type, Is.EqualTo(typeof(InvalidOperationException).FullName));
        Assert.That(snapshot.Root.TargetSite, Does.Contain(nameof(ThrowNested)));
        Assert.That(snapshot.Root.Properties.Single(property => property.Name == "Stack Trace").Value, Does.Contain(nameof(ThrowNested)));
        Assert.That(snapshot.Root.Children.Single().Type, Is.EqualTo(typeof(ArgumentException).FullName));
        Assert.That(snapshot.FullText, Does.Contain("root failure").And.Contain("inner argument").And.Contain(nameof(ThrowNested)));
    }

    [Test]
    public void NestedAggregateSiblingsKeepTheirOwnChildrenAndTheirActualInputOrder()
    {
        Exception first = new InvalidOperationException("first", new Exception("first child"));
        Exception second = new ApplicationException("second", new Exception("second child"));
        AggregateException nested = new("nested", new Exception("nested A"), new Exception("nested B"));
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(new AggregateException("all", first, second, nested));
        Assert.That(snapshot.Root.Children.Select(node => node.Message), Is.EqualTo(new[] { "first", "second", nested.Message }));
        Assert.That(snapshot.Root.Children[0].Children.Single().Message, Is.EqualTo("first child"));
        Assert.That(snapshot.Root.Children[1].Children.Single().Message, Is.EqualTo("second child"));
        Assert.That(snapshot.Root.Children[2].Children.Select(node => node.Message), Is.EqualTo(new[] { "nested A", "nested B" }));
        Assert.That(snapshot.FullText, Does.Contain("second child").And.Contain("nested B"));
    }

    [Test]
    public void ARepeatedAggregateEntryIsNotConfusedWithTheImplicitFirstInnerException()
    {
        Exception shared = new("shared");
        ExceptionReportNode root = ExceptionReportSnapshot.Capture(new AggregateException(shared, shared)).Root;
        Assert.That(root.Children.Count, Is.EqualTo(2), "These are two actual aggregate list positions, not InnerException plus the same first entry.");
        Assert.That(root.Children.Select(node => node.Message), Is.EqualTo(new[] { "shared", "shared" }));
    }

    [Test]
    public void DataThrowingGettersAndThrowingToStringCannotBreakTheReporter()
    {
        ExceptionReportSnapshot unreadable = ExceptionReportSnapshot.Capture(new BadDetailsException());
        Assert.That(unreadable.Root.Message, Is.EqualTo(ExceptionReportSnapshot.Unavailable));
        Assert.That(unreadable.Root.Properties.Single(property => property.Name == "Data").Value, Is.EqualTo(ExceptionReportSnapshot.Unavailable));
        Assert.That(unreadable.Root.Properties.Single(property => property.Name == "Broken").Value, Is.EqualTo(ExceptionReportSnapshot.Unavailable));
        Exception data = new("literal values"); data.Data["value"] = new BadString(); data.Data["null"] = null; data.Data["empty"] = "";
        ExceptionReportSnapshot values = ExceptionReportSnapshot.Capture(data);
        Assert.That(values.Root.Properties.Single(property => property.Name == "Data[\"value\"]").Value,
            Is.EqualTo(ExceptionReportSnapshot.Unavailable));
        Assert.That(values.Root.Properties.Single(property => property.Name == "Data[\"null\"]").Value, Is.EqualTo("<null>"));
        Assert.That(values.Root.Properties.Single(property => property.Name == "Data[\"empty\"]").Value, Is.Empty);
    }

    [Test]
    public void SnapshotStringsAndCollectionsStayImmutableWhenTheOriginalExceptionChanges()
    {
        StringBuilder value = new("before Ω"); Exception error = new("fixed"); error.Data["draft"] = value;
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(error); string text = snapshot.FullText;
        value.Append(" AFTER"); error.Data["draft"] = "replacement"; error.Data["new"] = "late";
        Assert.That(snapshot.Root.Properties.Single(property => property.Name == "Data[\"draft\"]").Value, Is.EqualTo("before Ω"));
        Assert.That(snapshot.FullText, Is.EqualTo(text));
        Assert.Throws<NotSupportedException>(() => ((IList<ExceptionReportProperty>)snapshot.Root.Properties).Add(new("late", "value")));
    }

    [Test]
    public void LongChainsAreProjectedWithoutRecursiveCaptureOrLosingTheLastException()
    {
        Exception error = new("deepest");
        for (int depth = 0; depth < 512; depth++) { error = new("level " + depth, error); }
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(error);
        ExceptionReportNode node = snapshot.Root;
        for (int depth = 0; depth < 512; depth++) { node = node.Children.Single(); }
        Assert.That(node.Message, Is.EqualTo("deepest")); Assert.That(node.Children, Is.Empty);
        Assert.That(snapshot.FullText, Does.Contain("deepest"));
    }

    [Test]
    public void GitErrorsKeepTheirActualArgumentsOutputAndExitCodeAsLocalDetails()
    {
        GitResult native = new(128, "stdout literal Ω", "stderr literal");
        GitCommandException error = new(["show", "a path Ω", "--"], native);
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(error);
        Assert.That(snapshot.Root.Properties.Single(property => property.Name == "Command").Value, Is.EqualTo("git show a path Ω --"));
        Assert.That(snapshot.Root.Properties.Single(property => property.Name == "Exit Code").Value, Is.EqualTo("128"));
        Assert.That(snapshot.FullText, Does.Contain(native.StandardOutput).And.Contain(native.StandardError));
    }

    [Test]
    public void PrefilledForkUriHasEscapedTemplateFieldsAndNoSubmissionSideEffect()
    {
        ExceptionReportSnapshot snapshot = ExceptionReportSnapshot.Capture(new Exception(new string('x', 85) + " & # Ω"));
        ExceptionReportContext context = new("operation & ?", "/private/path Ω", "version", DateTimeOffset.UnixEpoch, "OS", "CLR", "arch");
        Uri uri = DesktopErrorReporter.IssueUri(snapshot, context, "reviewed description & #");
        Dictionary<string, string> query = uri.Query[1..].Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));
        Assert.That(uri.GetLeftPart(UriPartial.Path), Is.EqualTo("https://github.com/Azreal42/gitextensions/issues/new"));
        Assert.That(query["title"].Length, Is.EqualTo(69)); Assert.That(query["title"], Does.EndWith("..."));
        Assert.That(query["template"], Is.EqualTo("bug_report.yml")); Assert.That(query["labels"], Is.EqualTo("type: NBug"));
        Assert.That(query["description"], Is.EqualTo(context.ExceptionText(snapshot, "reviewed description & #")));
        Assert.That(query["about"], Is.EqualTo(context.EnvironmentText));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowNested()
    { try { throw new ArgumentException("inner argument"); } catch (ArgumentException inner) { throw new InvalidOperationException("root failure", inner); } }
    private sealed class BadString { public override string ToString() => throw new InvalidOperationException("must stay local"); }
    private sealed class BadDetailsException : Exception
    {
        public override string Message => throw new InvalidOperationException("bad Message getter");
        public override IDictionary Data => throw new InvalidOperationException("bad Data getter");
        public string Broken => throw new InvalidOperationException("bad custom getter");
        public override string ToString() => throw new InvalidOperationException("bad ToString");
    }
}
