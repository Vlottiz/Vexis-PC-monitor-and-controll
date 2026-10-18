using System.Text.RegularExpressions;
using Xunit;

// Message names are plain strings typed on both sides: sendToHost({type:'fanSetManual'}) in a
// page, case "fanSetManual" in MainForm.Messages.cs. A typo on either side used to fail
// silently at runtime. This test fails the build instead.
public class MessageContractTests
{
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    static readonly Regex PageType = new(@"\{\s*type\s*:\s*['""]([A-Za-z]+)['""]", RegexOptions.Compiled);
    static readonly Regex HostCase = new(@"case ""([A-Za-z]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_message_a_page_sends_has_a_handler()
    {
        string root = RepoRoot();
        var handled = HostCase.Matches(File.ReadAllText(Path.Combine(root, "MainForm.Messages.cs")))
                              .Select(m => m.Groups[1].Value).ToHashSet();
        var missing = new List<string>();
        foreach (var f in Directory.GetFiles(root, "*.html").Concat(Directory.GetFiles(root, "nav*.js")))
            foreach (Match m in PageType.Matches(File.ReadAllText(f)))
                if (!handled.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(f)}: '{m.Groups[1].Value}'");
        Assert.True(missing.Count == 0, "Pages send message types nothing in MainForm.Messages.cs handles:\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void No_message_type_is_handled_twice()
    {
        var all = HostCase.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "MainForm.Messages.cs")))
                          .Select(m => m.Groups[1].Value).ToList();
        Assert.Empty(all.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key));
    }
}
