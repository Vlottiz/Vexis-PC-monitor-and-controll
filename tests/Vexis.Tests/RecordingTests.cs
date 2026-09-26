using Xunit;

// The CSV Recording page opens and deletes recordings by file name. Anything that
// isn't a plain file name in the recordings folder must be refused.
public class RecordingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"..\..\Windows\win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"sub\vexis-1.csv")]
    [InlineData("../vexis-1.csv")]
    public void Paths_are_refused(string? name) =>
        Assert.Null(CsvRecorder.ResolveFile(name));

    [Fact]
    public void Missing_files_are_refused() =>
        Assert.Null(CsvRecorder.ResolveFile("vexis-does-not-exist-" + Guid.NewGuid().ToString("N") + ".csv"));
}
