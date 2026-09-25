using System.Runtime.InteropServices;
using System.Security.Cryptography;

/// <summary>
/// Finds duplicate files (same content) under the folders the user picks, for the
/// Storage page.
///
/// Steps: list files → group by size → compare a quick hash of the first and last
/// 64 KB → full SHA-256 only for files that still match. Only files that really are
/// byte-for-byte identical end up in a group.
///
/// Safety:
///  • Removing sends files to the Recycle Bin — never a permanent delete.
///  • At least one copy of every group is always kept, whatever the page asks.
///  • Each file is hashed again right before it is removed, so a file that changed
///    since the scan is left alone.
///  • Windows / program folders are skipped unless asked for, and cloud-only
///    placeholders (OneDrive etc.) are never opened — reading them would download them.
/// </summary>
public sealed class DuplicateFinder
{
    public record DupFile(string path, string modified);
    public record DupGroup(int id, long size, List<DupFile> files);
    public record Progress(string stage, int files, int candidates, long hashedBytes, string current, bool done,
                           bool cancelled, string? error);
    public record Result(List<DupGroup> groups, int files, long wasted, int groupsTotal, double seconds);

    public record Options(List<string> paths, long minBytes, bool includeSystem);

    private CancellationTokenSource? _cts;
    private readonly Dictionary<int, (string hash, long size, HashSet<string> paths)> _groups = new();
    private readonly object _lock = new();
    public bool Running => _cts != null;

    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;   // cloud file, not on disk
    private const FileAttributes RecallOnOpen       = (FileAttributes)0x40000;

    private static readonly string[] SystemDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),   // ProgramData
    };
    private static readonly string[] SkipNames =
        { "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery", "Config.Msi", "Windows.old" };

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs the scan on a background thread, reporting progress about 3 times a second.</summary>
    public void Start(Options opt, Action<Progress> progress, Action<Result> done)
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int files = 0, candidates = 0; long hashed = 0; string current = "";
            var last = DateTime.MinValue;
            void Report(string stage, bool force = false)
            {
                if (!force && DateTime.Now - last < TimeSpan.FromMilliseconds(300)) return;
                last = DateTime.Now;
                progress(new Progress(stage, files, candidates, hashed, current, false, false, null));
            }
            try
            {
                // 1. List files by size
                var bySize = new Dictionary<long, List<FileInfo>>();
                foreach (var root in opt.paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Enumerate(new DirectoryInfo(root), opt.includeSystem, cts.Token))
                    {
                        files++;
                        if (f.Length < opt.minBytes) continue;
                        if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = new();
                        l.Add(f);
                        current = f.DirectoryName ?? "";
                        Report("Listing files");
                    }
                }
                var sizeGroups = bySize.Values.Where(l => l.Count > 1).ToList();
                candidates = sizeGroups.Sum(l => l.Count);
                Report("Comparing", true);

                // 2. Quick hash (first + last 64 KB), then 3. full hash for what still matches
                var groups = new List<(string hash, long size, List<FileInfo> files)>();
                foreach (var sg in sizeGroups)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    foreach (var quick in GroupBy(sg, f => QuickHash(f, ref hashed), cts.Token).Where(g => g.Count > 1))
                        foreach (var full in GroupByKeyed(quick, f => { current = f.FullName; Report("Checking contents"); return FullHash(f.FullName, ref hashed, cts.Token); }, cts.Token))
                            if (full.Value.Count > 1) groups.Add((full.Key, sg[0].Length, full.Value));
                }

                // Biggest savings first
                var ordered = groups.OrderByDescending(g => g.size * (g.files.Count - 1)).ToList();
                lock (_lock)
                {
                    _groups.Clear();
                    int id = 0;
                    foreach (var g in ordered) _groups[id++] = (g.hash, g.size, g.files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase));
                }
                var shown = ordered.Take(500).Select((g, i) => new DupGroup(i, g.size,
                    g.files.OrderBy(f => f.LastWriteTime).Select(f => new DupFile(f.FullName, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"))).ToList())).ToList();
                long wasted = ordered.Sum(g => g.size * (g.files.Count - 1));
                Console.WriteLine($"[dupes] {files} files, {ordered.Count} duplicate groups, {wasted / 1048576.0:F0} MB reclaimable, {sw.Elapsed.TotalSeconds:F0} s");
                done(new Result(shown, files, wasted, ordered.Count, sw.Elapsed.TotalSeconds));
                progress(new Progress("Done", files, candidates, hashed, "", true, false, null));
            }
            catch (OperationCanceledException)
            {
                progress(new Progress("Cancelled", files, candidates, hashed, "", true, true, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dupes] Scan failed: {ex.Message}");
                progress(new Progress("Failed", files, candidates, hashed, "", true, false, ex.Message));
            }
            finally { _cts = null; cts.Dispose(); }
        });
    }

    private static IEnumerable<FileInfo> Enumerate(DirectoryInfo root, bool includeSystem, CancellationToken ct)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            FileSystemInfo[] items;
            try { items = dir.GetFileSystemInfos(); } catch { continue; }   // no access
            foreach (var item in items)
            {
                var a = item.Attributes;
                if ((a & FileAttributes.ReparsePoint) != 0) continue;       // junctions / links: avoid loops
                if (item is DirectoryInfo d)
                {
                    if (SkipNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!includeSystem && (IsSystemDir(d.FullName) || d.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase))) continue;
                    stack.Push(d);
                }
                else if (item is FileInfo f)
                {
                    if ((a & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0) continue;   // cloud-only
                    if (!includeSystem && (a & FileAttributes.System) != 0) continue;
                    yield return f;
                }
            }
        }
    }

    private static bool IsSystemDir(string path) =>
        SystemDirs.Any(s => !string.IsNullOrEmpty(s) && path.Equals(s, StringComparison.OrdinalIgnoreCase));

    private static List<List<FileInfo>> GroupBy(List<FileInfo> files, Func<FileInfo, string?> key, CancellationToken ct) =>
        GroupByKeyed(files, key, ct).Values.ToList();

    private static Dictionary<string, List<FileInfo>> GroupByKeyed(List<FileInfo> files, Func<FileInfo, string?> key, CancellationToken ct)
    {
        var map = new Dictionary<string, List<FileInfo>>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string? k = key(f);
            if (k == null) continue;                                        // unreadable: skip
            if (!map.TryGetValue(k, out var l)) map[k] = l = new();
            l.Add(f);
        }
        return map;
    }

    private static string? QuickHash(FileInfo f, ref long hashed)
    {
        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            const int Chunk = 65536;
            var buf = new byte[Math.Min(fs.Length, Chunk * 2)];
            int n = fs.Read(buf, 0, (int)Math.Min(Chunk, fs.Length));
            if (fs.Length > Chunk)
            {
                fs.Seek(Math.Max(Chunk, fs.Length - Chunk), SeekOrigin.Begin);
                n += fs.Read(buf, n, buf.Length - n);
            }
            hashed += n;
            return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n)));
        }
        catch { return null; }
    }

    private static string? FullHash(string path, ref long hashed, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
                hashed += n;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── Recycle Bin (SHFileOperation with undo — the same as Delete in Explorer) ──
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo;
        public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;

    private static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    /// <summary>
    /// Sends the given files to the Recycle Bin. Refuses anything that isn't in the
    /// last scan's results, would leave a group with no copy, or changed since the scan.
    /// </summary>
    public (int removed, long freed, List<string> skipped) Recycle(List<string> paths)
    {
        var skipped = new List<string>();
        int removed = 0; long freed = 0;
        var want = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string hash, long size, HashSet<string> paths)> groups;
        lock (_lock) groups = _groups.Values.ToList();

        foreach (var g in groups)
        {
            var selected = g.paths.Where(want.Contains).ToList();
            if (selected.Count == 0) continue;
            // Copies that stay: not selected and still there with the same content
            var keepers = g.paths.Except(selected, StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            if (keepers.Count == 0)
            {
                // Keep the oldest selected copy
                var keep = selected.Where(File.Exists).OrderBy(p => File.GetLastWriteTime(p)).FirstOrDefault();
                if (keep == null) continue;
                selected.Remove(keep);
                skipped.Add(keep + " (kept — the last copy is never removed)");
            }
            foreach (var p in selected)
            {
                long dummy = 0;
                if (!File.Exists(p)) { skipped.Add(p + " (already gone)"); continue; }
                if (new FileInfo(p).Length != g.size || FullHash(p, ref dummy, CancellationToken.None) != g.hash)
                { skipped.Add(p + " (changed since the scan)"); continue; }
                if (SendToRecycleBin(p)) { removed++; freed += g.size; g.paths.Remove(p); }
                else skipped.Add(p + " (couldn't move it to the Recycle Bin)");
            }
        }
        foreach (var p in want.Where(p => !groups.Any(g => g.paths.Contains(p)) && File.Exists(p) && !skipped.Any(s => s.StartsWith(p))))
            skipped.Add(p + " (not in the scan results)");
        Console.WriteLine($"[dupes] Recycled {removed} file(s), {freed / 1048576.0:F0} MB; {skipped.Count} skipped");
        return (removed, freed, skipped);
    }
}
