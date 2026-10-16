using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 1 || !Directory.Exists(args[0]))
{ Console.Error.WriteLine("Usage: WindowInventory <repository-root>"); return 2; }
string root = Path.GetFullPath(args[0]);
ProcessStartInfo git = new("git") { WorkingDirectory = root, RedirectStandardOutput = true };
git.ArgumentList.Add("ls-files"); git.ArgumentList.Add("--cached"); git.ArgumentList.Add("--others"); git.ArgumentList.Add("--exclude-standard"); git.ArgumentList.Add("-z"); git.ArgumentList.Add("--");
foreach (string p in new[] { "src/app", "src/plugins", "src/linux", "tests/linux" }) { git.ArgumentList.Add(p); }
using Process child = Process.Start(git)!;
string[] paths = child.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".cs", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToArray();
child.WaitForExit(); if (child.ExitCode != 0) { throw new InvalidOperationException("git ls-files failed"); }
List<object> types = [], objects = [], calls = [], diagnostics = [];
Dictionary<string, string> fileHashes = new(StringComparer.Ordinal);
foreach (string path in paths)
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(root, path));
    fileHashes.Add(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    using StreamReader reader = new(new MemoryStream(bytes));
    string source = reader.ReadToEnd();
    foreach (string mode in new[] { "release", "debug" })
    {
        bool linux = path.StartsWith("src/linux/", StringComparison.Ordinal) || path.StartsWith("tests/linux/", StringComparison.Ordinal);
        string[] platform = linux ? ["LINUX", "NET10_0"] : ["WINDOWS", "NET10_0", "NET10_0_WINDOWS"];
        string[] symbols = mode == "debug" ? [.. platform, "DEBUG", "TRACE"] : [.. platform, "TRACE"];
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols), path);
        SyntaxNode syntax = tree.GetRoot();
        foreach (Diagnostic error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)) { diagnostics.Add(new { path, mode, message = error.ToString() }); }
        string Id(ClassDeclarationSyntax c) => string.Join('.', c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).Concat(c.Ancestors().OfType<ClassDeclarationSyntax>().Reverse().Select(n => n.Identifier.ValueText)).Append(c.Identifier.ValueText + (c.TypeParameterList is null ? "" : "`" + c.TypeParameterList.Parameters.Count)));
        string Scope(SyntaxNode node) => node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
            ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<initializer>";
        foreach (ClassDeclarationSyntax c in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            string[] bases = c.BaseList?.Types.Select(t => t.Type.ToString()).ToArray() ?? [];
            types.Add(new { id = Id(c), name = c.Identifier.ValueText, path, line = tree.GetLineSpan(c.Identifier.Span).StartLinePosition.Line + 1,
                mode, bases, @abstract = c.Modifiers.Any(SyntaxKind.AbstractKeyword), @static = c.Modifiers.Any(SyntaxKind.StaticKeyword),
                methods = c.Members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.ValueText).ToArray(),
                members = c.Members.OfType<MethodDeclarationSyntax>().Select(m => new { name = m.Identifier.ValueText, line = tree.GetLineSpan(m.Identifier.Span).StartLinePosition.Line + 1 }).ToArray(),
                fields = c.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables.Select(v => new { name = v.Identifier.ValueText, type = f.Declaration.Type.ToString() })).ToArray() });
        }
        foreach (BaseObjectCreationExpressionSyntax creation in syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            string? type = (creation as ObjectCreationExpressionSyntax)?.Type.ToString();
            if (type is null && creation.Parent is EqualsValueClauseSyntax eq && eq.Parent is VariableDeclaratorSyntax variable && variable.Parent is VariableDeclarationSyntax declaration) { type = declaration.Type.ToString(); }
            if (type is null && creation.Parent is ArrowExpressionClauseSyntax arrow && arrow.Parent is MethodDeclarationSyntax method) { type = method.ReturnType.ToString(); }
            ClassDeclarationSyntax? owner = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            objects.Add(new { type, owner = owner is null ? null : Id(owner), path, line = tree.GetLineSpan(creation.Span).StartLinePosition.Line + 1, method = Scope(creation), mode,
                text = creation.ToString().Split('\n')[0][..Math.Min(creation.ToString().Split('\n')[0].Length, 180)] });
        }
        foreach (InvocationExpressionSyntax call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string expression = call.Expression.ToString();
            if (!new[] { "ShowDialog", "MessageBox", "MessageBoxes", "TaskDialog", "PromptAsync", "ConfirmAsync", "ShowErrorAsync", "OpenFilePicker", "SaveFilePicker", "OpenFolderPicker", "GetSettingsControl", "ShowSettings" }.Any(expression.Contains)) { continue; }
            ClassDeclarationSyntax? owner = call.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            calls.Add(new { expression, owner = owner is null ? null : Id(owner), path, line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1, method = Scope(call), mode });
        }
    }
}
Console.Write(JsonSerializer.Serialize(new { files = paths, file_hashes = fileHashes, types, objects, calls, diagnostics }, new JsonSerializerOptions { WriteIndented = true }));

return 0;
