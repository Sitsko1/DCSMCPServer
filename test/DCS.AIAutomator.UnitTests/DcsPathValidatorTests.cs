
public class DcsPathValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DcsPathValidatorTests_" + Guid.NewGuid());

    public DcsPathValidatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeInstall(string binDir = "bin")
    {
        string dir = Path.Combine(_root, "DCS World");
        Directory.CreateDirectory(Path.Combine(dir, binDir));
        Directory.CreateDirectory(Path.Combine(dir, "Config")); // real installs have one too
        File.WriteAllText(Path.Combine(dir, binDir, "DCS.exe"), "");
        return dir;
    }

    private string MakeSavedGames()
    {
        string dir = Path.Combine(_root, "SavedGames", "DCS");
        Directory.CreateDirectory(Path.Combine(dir, "Config"));
        return dir;
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("bin-mt")]
    public void InstallPath_Valid_WhenDcsExePresent(string binDir) =>
        Assert.Null(DcsPathValidator.ValidateInstallPath(MakeInstall(binDir)));

    [Fact]
    public void InstallPath_Valid_WhenEmpty_SinceItIsOptional() =>
        Assert.Null(DcsPathValidator.ValidateInstallPath(""));

    [Fact]
    public void InstallPath_Invalid_WhenSavedGamesFolderGiven() =>
        Assert.NotNull(DcsPathValidator.ValidateInstallPath(MakeSavedGames()));

    [Fact]
    public void InstallPath_Invalid_WhenMissing() =>
        Assert.NotNull(DcsPathValidator.ValidateInstallPath(Path.Combine(_root, "nope")));

    [Fact]
    public void SavedGamesPath_Valid_WhenConfigPresent() =>
        Assert.Null(DcsPathValidator.ValidateSavedGamesPath(MakeSavedGames()));

    [Fact]
    public void SavedGamesPath_Invalid_WhenInstallFolderGiven()
    {
        string error = DcsPathValidator.ValidateSavedGamesPath(MakeInstall())!;
        Assert.Contains("install", error);
    }

    [Fact]
    public void SavedGamesPath_Invalid_WhenNoConfigFolder() =>
        Assert.NotNull(DcsPathValidator.ValidateSavedGamesPath(_root));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SavedGamesPath_Invalid_WhenBlank(string path) =>
        Assert.NotNull(DcsPathValidator.ValidateSavedGamesPath(path));

    [Fact]
    public void Deploy_RefusesInstallFolder_AndWritesNothing()
    {
        string install = MakeInstall();

        var result = LuaScriptDeployer.Deploy(install, "127.0.0.1", 1024, "TestLinkSecret_0123456789");

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(install, "Scripts")));
    }
}
