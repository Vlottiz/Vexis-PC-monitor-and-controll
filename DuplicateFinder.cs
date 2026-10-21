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
///  • Removing sends files to the Recycle Bin. Files on drives without one (USB, network) are
///    skipped, and if Windows still can't recycle a file (e.g. too big for the bin) it asks
///    before deleting it for good — Vexis never deletes permanently on its own.
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

    public void Cancel() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }   // scan just finished

    /// <summary>True only for a file that the last scan listed as a duplicate.</summary>
    public bool IsInResults(string? path)
    {
        if (!IsPlainPath(path)) return false;
        lock (_lock) return _groups.Values.Any(g => g.paths.Contains(path!));
    }

    /// <summary>A scan folder from the page: a full local path to a folder that exists.</summary>
    internal static string? CheckFolder(string? path)
    {
        if (!IsPlainPath(path)) return null;
        string full = Path.GetFullPath(path!);
        return Directory.Exists(full) ? RealFolder(full) : null;
    }

    /// <summary>
    /// The folder's real location, following junctions and links anywhere in the path.
    /// Without this, two roots pointing at the same folder would list every file twice,
    /// and each file would look like a duplicate of itself — removing the "copy" would
    /// remove the only one.
    /// </summary>
    internal static string RealFolder(string dir)
    {
        if (!OperatingSystem.IsWindows()) return dir;
        using var h = CreateFileW(dir, 0, 7 /* share read|write|delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                  0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: open a folder */, IntPtr.Zero);
        if (h.IsInvalid) return dir;
        var sb = new System.Text.StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return dir;
        string real = sb.ToString();
        if (real.StartsWith(@"\\?\UNC\")) real = @"\\" + real[8..];
        else if (real.StartsWith(@"\\?\")) real = real[4..];
        return real;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, System.Text.StringBuilder path, uint len, uint flags);

    /// <summary>Real folders, without repeats, and without folders inside another root (already scanned).</summary>
    internal static List<string> DistinctRoots(IEnumerable<string> roots, Func<string, string> real)
    {
        var list = roots.Where(Directory.Exists).Select(r => real(r).TrimEnd('\\', '/'))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Where(r => !list.Any(o => o != r &&
                       r.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // Full path, no wildcards, quotes or device prefixes (\\?\, \\.\)
    private static bool IsPlainPath(string? p) =>
        !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) &&
        p.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|', '\0' }) < 0 &&
        !p.StartsWith(@"\\?\") && !p.StartsWith(@"\\.\");

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
                foreach (var root in DistinctRoots(opt.paths, RealFolder))
                {
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
            // ReadExactly: a single Read may return fewer bytes than asked
            int n = (int)Math.Min(Chunk, fs.Length);
            fs.ReadExactly(buf, 0, n);
            if (fs.Length > Chunk)
            {
                fs.Seek(Math.Max(Chunk, fs.Length - Chunk), SeekOrigin.Begin);
                fs.ReadExactly(buf, n, buf.Length - n);
                n = buf.Length;
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
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400,
                         FOF_WANTNUKEWARNING = 0x4000;   // ask first if the file can't go to the Recycle Bin

    // Only fixed local drives have a Recycle Bin; USB sticks and network shares don't
    private static bool HasRecycleBin(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed; }
        catch { return false; }
    }

    private static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = path + "\0\0",
            // FOF_NOCONFIRMATION alone would delete for good where there's no Recycle Bin (USB and
            // network drives, files too big for the bin). FOF_WANTNUKEWARNING makes Windows ask first.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI
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
        var want = paths.Where(IsPlainPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string hash, long size, HashSet<string> paths)> groups;
        lock (_lock) groups = _groups.Values.ToList();

        foreach (var g in groups)
        {
            var selected = g.paths.Where(want.Contains).ToList();
            if (selected.Count == 0) continue;
            // Copies that stay: not selected and still there. At least one of them must still have
            // the scanned content, or "removing a duplicate" could remove the last real copy
            var keepers = g.paths.Except(selected, StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
            long scratch = 0;
            if (keepers.Count > 0 && !keepers.Any(k => FullHash(k, ref scratch, CancellationToken.None) == g.hash))
            {
                foreach (var p in selected) skipped.Add(p + " (the copies that would stay changed since the scan — scan again)");
                continue;
            }
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
                if (!HasRecycleBin(p)) { skipped.Add(p + " (not on a local drive with a Recycle Bin)"); continue; }
                if (new FileInfo(p).Length != g.size || FullHash(p, ref dummy, CancellationToken.None) != g.hash)
                { skipped.Add(p + " (changed since the scan)"); continue; }
                if (SendToRecycleBin(p)) { removed++; freed += g.size; lock (_lock) g.paths.Remove(p); }
                else skipped.Add(p + " (couldn't move it to the Recycle Bin)");
            }
        }
        foreach (var p in want.Where(p => !groups.Any(g => g.paths.Contains(p)) && File.Exists(p) && !skipped.Any(s => s.StartsWith(p))))
            skipped.Add(p + " (not in the scan results)");
        Console.WriteLine($"[dupes] Recycled {removed} file(s), {freed / 1048576.0:F0} MB; {skipped.Count} skipped");
        return (removed, freed, skipped);
    }
}
