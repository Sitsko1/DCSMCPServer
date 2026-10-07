public class DcsProcessTests : IDisposable
{
    private readonly string _install = Directory.CreateTempSubdirectory("dcs-install-").FullName;

    public void Dispose() => Directory.Delete(_install, recursive: true);

    private string MakeExe(string folder)
    {
        string exe = Path.Combine(_install, folder, "DCS.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void Executable_PicksTheMultithreadedOrClassicBuild()
    {
        string mt = MakeExe("bin-mt");
        string classic = MakeExe("bin");

        Assert.Equal((mt, null), DcsProcess.Executable(_install, multithreaded: true));
        Assert.Equal((classic, null), DcsProcess.Executable(_install, multithreaded: false));
    }

    [Fact]
    public void Executable_Missing_SaysWhereItLooked()
    {
        MakeExe("bin");

        var (path, error) = DcsProcess.Executable(_install, multithreaded: true);

        Assert.Null(path);
        Assert.Contains(Path.Combine(_install, "bin-mt", "DCS.exe"), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Executable_WithoutAnInstallPath_PointsToSettings(string installPath)
    {
        var (path, error) = DcsProcess.Executable(installPath, multithreaded: true);

        Assert.Null(path);
        Assert.Contains("Settings → Paths", error);
    }
}
