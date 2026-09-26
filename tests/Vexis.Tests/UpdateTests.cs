using Xunit;

public class UpdateTests
{
    private const string Release = """
    {
      "tag_name": "v2.11.0",
      "body": "notes",
      "assets": [
        { "name": "source.zip", "browser_download_url": "https://x/source.zip", "size": 10 },
        { "name": "Other.exe", "browser_download_url": "https://x/Other.exe", "size": 20 },
        { "name": "VexisHM-Setup-2.11.0.exe", "browser_download_url": "https://x/setup.exe", "size": 30 },
        { "name": "VexisHM-Setup-2.11.0.exe.sha256", "browser_download_url": "https://x/setup.sha256", "size": 90 }
      ]
    }
    """;

    [Fact]
    public void Reads_version_installer_and_checksum()
    {
        var r = UpdateManager.ParseRelease(Release)!;
        Assert.Equal(new Version(2, 11, 0), r.Version);
        Assert.Equal("https://x/setup.exe", r.InstallerUrl);          // the Setup exe wins over other .exe files
        Assert.Equal(30, r.InstallerSize);
        Assert.Equal("https://x/setup.sha256", r.ChecksumUrl);
    }

    [Theory]
    [InlineData("2.11.0")]
    [InlineData("V2.11.0")]
    public void Tag_with_or_without_v_is_accepted(string tag) =>
        Assert.NotNull(UpdateManager.ParseRelease($$"""{"tag_name":"{{tag}}","assets":[]}"""));

    [Theory]
    [InlineData("Vexis")]
    [InlineData("beta")]
    public void Tag_that_is_not_a_version_is_ignored(string tag) =>
        Assert.Null(UpdateManager.ParseRelease($$"""{"tag_name":"{{tag}}","assets":[]}"""));

    [Fact]
    public void Release_without_a_checksum_file_still_parses()
    {
        var r = UpdateManager.ParseRelease("""{"tag_name":"v2.10.0","assets":[{"name":"VexisHM-Setup.exe","browser_download_url":"u","size":1}]}""")!;
        Assert.Null(r.ChecksumUrl);
        Assert.Equal("u", r.InstallerUrl);
    }

    [Theory]
    [InlineData("2.11.0", "2.10.1", true)]
    [InlineData("2.11.0", "2.11.0", false)]
    [InlineData("2.10.0", "2.11.0", false)]
    [InlineData("2.10.10", "2.10.9", true)]      // numeric, not alphabetical
    [InlineData("3.0.0", "not a version", false)]
    public void Only_newer_versions_are_offered(string latest, string installed, bool expected)
    {
        var r = new UpdateManager.ReleaseInfo("v" + latest, Version.Parse(latest), "u", 1, "");
        Assert.Equal(expected, UpdateManager.IsNewer(r, installed));
    }

    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void Checksum_file_in_sha256sum_format() =>
        Assert.Equal(Hash, UpdateManager.ParseChecksum($"{Hash.ToUpperInvariant()}  VexisHM-Setup.exe\n", "VexisHM-Setup.exe"));

    [Fact]
    public void Checksum_file_with_a_bare_hash() =>
        Assert.Equal(Hash, UpdateManager.ParseChecksum(Hash + "\r\n", "VexisHM-Setup.exe"));

    [Fact]
    public void Checksum_for_a_different_file_is_not_used() =>
        Assert.Null(UpdateManager.ParseChecksum($"{Hash}  something-else.exe", "VexisHM-Setup.exe"));

    [Fact]
    public void Garbage_is_not_a_checksum() =>
        Assert.Null(UpdateManager.ParseChecksum("<html>404</html>", "VexisHM-Setup.exe"));
}
