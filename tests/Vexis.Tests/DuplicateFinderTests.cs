using Xunit;

// The duplicate finder can send files to the Recycle Bin, so "same contents"
// must really mean the same bytes.
public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vexis-dup-tests-" + Guid.NewGuid().ToString("N"));

    public DuplicateFinderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, byte[] data)
    {
        string p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] Bytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private DuplicateFinder.Result Scan(long minBytes = 0)
    {
        var tcs = new TaskCompletionSource<DuplicateFinder.Result>();
        string? error = null;
        new DuplicateFinder().Start(new DuplicateFinder.Options(new() { _dir }, minBytes, includeSystem: false),
            p => { if (p.done && p.error != null) error = p.error; if (p.done && (p.cancelled || p.error != null)) tcs.TrySetException(new Exception(error ?? "cancelled")); },
            r => tcs.TrySetResult(r));
        Assert.True(tcs.Task.Wait(TimeSpan.FromSeconds(30)), "scan timed out");
        return tcs.Task.Result;
    }

    [Fact]
    public void Identical_files_are_grouped()
    {
        var data = Bytes(300_000, 1);
        Write("a/photo.jpg", data);
        Write("b/photo (1).jpg", data);
        Write("c/other.jpg", Bytes(300_000, 2));
        var r = Scan();
        var g = Assert.Single(r.groups);
        Assert.Equal(2, g.files.Count);
        Assert.Equal(300_000, r.wasted);
    }

    // Same size and same first/last 64 KB, but a different middle: the quick check
    // alone would call these duplicates, the full hash must not.
    [Fact]
    public void Files_that_differ_only_in_the_middle_are_not_duplicates()
    {
        var a = Bytes(400_000, 3);
        var b = (byte[])a.Clone();
        b[200_000] ^= 0xFF;
        Write("x.bin", a);
        Write("y.bin", b);
        Assert.Empty(Scan().groups);
    }

    [Fact]
    public void Files_below_the_minimum_size_are_skipped()
    {
        var small = Bytes(1000, 4);
        Write("s1.txt", small);
        Write("s2.txt", small);
        Assert.Empty(Scan(minBytes: 1024 * 1024).groups);
        Assert.Single(Scan(minBytes: 0).groups);
    }

    [Fact]
    public void Biggest_savings_come_first()
    {
        var big = Bytes(500_000, 5); var small = Bytes(10_000, 6);
        Write("big1", big); Write("big2", big);
        Write("small1", small); Write("small2", small); Write("small3", small);
        var r = Scan();
        Assert.Equal(2, r.groups.Count);
        Assert.Equal(500_000, r.groups[0].size);
    }
}
