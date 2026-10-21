using System.Text.RegularExpressions;
using Xunit;

// Pages must never build inline handler code (onclick="...") out of outside text such as
// file, process or device names: a name like  x'});alert(1);({'  would run as script.
// Outside text goes in a data-* attribute or an array the handler looks up instead.
// (Escaping for HTML doesn't help here: the browser un-escapes the attribute before running it.)
//
// This test finds every ${...} inside an on*= attribute and only allows the expressions
// listed below, all of which are Vexis's own values. A new one fails until it's checked
// and added here.
public class PageHandlerTests
{
    static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["fans.html"]      = new[] { "fanArg(fan.name)", "fanArg(name)", "i", "id" },  // fanArg → FAN_NAMES[n]; id is sid() (safe characters only)
        ["home.html"]      = new[] { "NAV_PAGE_FILES[p.id]" },
        ["nav.js"]         = new[] { "file", "i", "key", "s", "p.id" },               // built-in page list, colour keys, slot numbers
        ["processes.html"] = new[] { "Number(p.pid) | 0", "k" },                      // number; column key
        ["record.html"]    = new[] { "i" },
        ["rgb.html"]       = new[] { "a.id", "c", "cnt", "i", "isR?'applyRAM':'setDC'", "li", "mi" },
        ["storage.html"]   = new[] { "i" },
        ["threads.html"]   = new[] { "c.key" },
    };

    static readonly Regex Handler = new(@"\bon[a-z]+=(""[^""]*""|'[^']*')", RegexOptions.Compiled);
    static readonly Regex Interp  = new(@"\$\{([^}]*)\}", RegexOptions.Compiled);

    // tests/Vexis.Tests/PageHandlerTests.cs → the repository root two folders up
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void Inline_handlers_only_use_Vexis_own_values()
    {
        string root = RepoRoot();
        var files = Directory.GetFiles(root, "*.html").Concat(Directory.GetFiles(root, "nav*.js"));
        var problems = new List<string>();

        foreach (var path in files)
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            // nav.js is split into nav-*.js files; they share one list
            var ok = Allowed.TryGetValue(name.StartsWith("nav") && name.EndsWith(".js") ? "nav.js" : name, out var list) ? list : Array.Empty<string>();
            foreach (Match h in Handler.Matches(text))
                foreach (Match e in Interp.Matches(h.Value))
                {
                    string expr = e.Groups[1].Value.Trim();
                    if (!ok.Contains(expr))
                    {
                        int line = text.Take(h.Index).Count(ch => ch == '\n') + 1;
                        problems.Add($"{name}:{line}  ${{{expr}}}");
                    }
                }
        }
        Assert.True(problems.Count == 0,
            "Outside text may be built into inline handler code. Use data-* or an index lookup:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void The_check_catches_the_old_SHOW_button()
    {
        const string bad = """<button onclick="sendToHost({type:'showRecording',file:'${esc(f.name)}'})">SHOW</button>""";
        var exprs = Handler.Matches(bad).SelectMany(h => Interp.Matches(h.Value)).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "esc(f.name)" }, exprs);
    }
}
