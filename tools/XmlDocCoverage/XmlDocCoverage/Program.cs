using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private static int Main(string[] args)
    {
        string repoRoot = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        string[] roots =
        [
            Path.Combine(repoRoot, "src", "MikrotikWorker"),
            Path.Combine(repoRoot, "tests", "MikrotikWorker.Tests"),
        ];

        var findings = new List<Finding>();
        int eligible = 0;
        int documented = 0;
        int files = 0;

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"Missing root: {root}");
                return 2;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files++;
                string text = File.ReadAllText(path);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(text, path: path);
                CompilationUnitSyntax rootNode = tree.GetCompilationUnitRoot();

                foreach (MemberDeclarationSyntax member in rootNode.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    if (!IsEligible(member, out string kind, out string name))
                    {
                        continue;
                    }

                    eligible++;
                    bool hasDoc = HasMeaningfulXmlDoc(member);
                    if (hasDoc)
                    {
                        documented++;
                    }
                    else
                    {
                        findings.Add(new Finding(
                            Relative(repoRoot, path),
                            member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            kind,
                            name));
                    }
                }
            }
        }

        var byProject = findings
            .GroupBy(f => f.File.StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || f.File.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                ? "tests/MikrotikWorker.Tests"
                : "src/MikrotikWorker")
            .ToDictionary(g => g.Key, g => g.ToImmutableArray());

        Console.WriteLine($"RepoRoot={repoRoot}");
        Console.WriteLine($"Files={files}");
        Console.WriteLine($"Eligible={eligible}");
        Console.WriteLine($"Documented={documented}");
        Console.WriteLine($"Missing={findings.Count}");
        double pct = eligible == 0 ? 100.0 : (100.0 * documented / eligible);
        Console.WriteLine($"Coverage={pct:0.00}%");
        Console.WriteLine();

        foreach (string project in new[] { "src/MikrotikWorker", "tests/MikrotikWorker.Tests" })
        {
            int projectEligible = CountEligibleIn(roots[project.StartsWith("tests", StringComparison.Ordinal) ? 1 : 0], repoRoot);
            int projectMissing = byProject.TryGetValue(project, out var list) ? list.Length : 0;
            int projectDocumented = projectEligible - projectMissing;
            double projectPct = projectEligible == 0 ? 100.0 : (100.0 * projectDocumented / projectEligible);
            Console.WriteLine($"Project={project}; Eligible={projectEligible}; Documented={projectDocumented}; Missing={projectMissing}; Coverage={projectPct:0.00}%");
        }

        Console.WriteLine();
        Console.WriteLine("MISSING");
        foreach (Finding f in findings.OrderBy(f => f.File).ThenBy(f => f.Line))
        {
            Console.WriteLine($"{f.File}:{f.Line}:{f.Kind}:{f.Name}");
        }

        string reportPath = Path.Combine(repoRoot, "tools", "XmlDocCoverage", "coverage-report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var sb = new StringBuilder();
        sb.AppendLine($"Eligible={eligible}");
        sb.AppendLine($"Documented={documented}");
        sb.AppendLine($"Missing={findings.Count}");
        sb.AppendLine($"Coverage={pct:0.00}%");
        foreach (Finding f in findings.OrderBy(f => f.File).ThenBy(f => f.Line))
        {
            sb.AppendLine($"{f.File}:{f.Line}:{f.Kind}:{f.Name}");
        }

        File.WriteAllText(reportPath, sb.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote {reportPath}");

        return findings.Count == 0 ? 0 : 1;
    }

    private static int CountEligibleIn(string root, string repoRoot)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
            foreach (MemberDeclarationSyntax member in tree.GetCompilationUnitRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                if (IsEligible(member, out _, out _))
                {
                    count++;
                }
            }
        }

        _ = repoRoot;
        return count;
    }

    private static bool IsEligible(MemberDeclarationSyntax member, out string kind, out string name)
    {
        kind = member.Kind().ToString();
        name = "?";

        // Skip generated / non-declarative noise.
        if (member is GlobalStatementSyntax or IncompleteMemberSyntax)
        {
            return false;
        }

        // Namespace members: types and nested members handled via DescendantNodes.
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax:
                return false;
            case ClassDeclarationSyntax c:
                kind = "class";
                name = c.Identifier.Text;
                return true;
            case StructDeclarationSyntax s:
                kind = "struct";
                name = s.Identifier.Text;
                return true;
            case InterfaceDeclarationSyntax i:
                kind = "interface";
                name = i.Identifier.Text;
                return true;
            case RecordDeclarationSyntax r:
                kind = "record";
                name = r.Identifier.Text;
                // Record primary constructor parameters are covered via the record type summary;
                // still require the type itself to be documented.
                return true;
            case EnumDeclarationSyntax e:
                kind = "enum";
                name = e.Identifier.Text;
                return true;
            case EnumMemberDeclarationSyntax em:
                kind = "enum-member";
                name = em.Identifier.Text;
                return true;
            case DelegateDeclarationSyntax d:
                kind = "delegate";
                name = d.Identifier.Text;
                return true;
            case MethodDeclarationSyntax m:
                if (m.ExplicitInterfaceSpecifier is not null)
                {
                    // Explicit interface implementations rarely accept independent XML docs
                    // in a useful way; still require a summary when present as a member.
                }

                kind = "method";
                name = m.Identifier.Text + FormatParams(m.ParameterList);
                return true;
            case ConstructorDeclarationSyntax ctor:
                kind = "ctor";
                name = ctor.Identifier.Text + FormatParams(ctor.ParameterList);
                return true;
            case DestructorDeclarationSyntax:
                kind = "dtor";
                name = "Finalize";
                return true;
            case PropertyDeclarationSyntax p:
                kind = "property";
                name = p.Identifier.Text;
                return true;
            case IndexerDeclarationSyntax idx:
                kind = "indexer";
                name = "this" + FormatParams(idx.ParameterList);
                return true;
            case EventDeclarationSyntax ev:
                kind = "event";
                name = ev.Identifier.Text;
                return true;
            case EventFieldDeclarationSyntax efd:
                kind = "event-field";
                name = string.Join(", ", efd.Declaration.Variables.Select(v => v.Identifier.Text));
                return true;
            case FieldDeclarationSyntax fd:
                // Skip backing fields synthesized in records? Hand-written fields only.
                kind = "field";
                name = string.Join(", ", fd.Declaration.Variables.Select(v => v.Identifier.Text));
                return true;
            case OperatorDeclarationSyntax op:
                kind = "operator";
                name = op.OperatorToken.Text + FormatParams(op.ParameterList);
                return true;
            case ConversionOperatorDeclarationSyntax conv:
                kind = "conversion";
                name = conv.Type.ToString() + FormatParams(conv.ParameterList);
                return true;
            default:
                return false;
        }
    }

    private static string FormatParams(ParameterListSyntax? parameters)
    {
        if (parameters is null)
        {
            return "()";
        }

        return "(" + string.Join(", ", parameters.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")";
    }

    private static string FormatParams(BracketedParameterListSyntax parameters)
        => "[" + string.Join(", ", parameters.Parameters.Select(p => p.Type?.ToString() ?? "?")) + "]";

    private static bool HasMeaningfulXmlDoc(MemberDeclarationSyntax member)
    {
        // Documentation comments are trivia on the first token of the member.
        SyntaxTriviaList leading = member.GetLeadingTrivia();
        var docs = leading
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();

        if (docs.Count == 0)
        {
            // Also accept documentation attached to modifiers via previous sibling trivia edge cases.
            return false;
        }

        string text = string.Concat(docs.Select(d => d.ToFullString()));

        // <inheritdoc/> is valid, meaningful documentation for interface / base implementations.
        if (Regex.IsMatch(text, @"<\s*inheritdoc\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (!text.Contains("<summary>", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject empty / whitespace-only / placeholder summaries.
        Match m = Regex.Match(
            text,
            @"<summary>\s*(?<body>.*?)\s*</summary>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return false;
        }

        string body = Regex.Replace(m.Groups["body"].Value, @"///\s?", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        // Extremely weak placeholders.
        if (body is "TODO" or "TBD" or "." or "...")
        {
            return false;
        }

        return true;
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private sealed record Finding(string File, int Line, string Kind, string Name);
}
