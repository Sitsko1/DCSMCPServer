public class AutoexecConfigTests : IDisposable
{
    private readonly string _savedGames = Directory.CreateTempSubdirectory("dcs-autoexec-").FullName;
    private string ConfigPath => AutoexecConfig.PathFor(_savedGames);

    public AutoexecConfigTests() => Directory.CreateDirectory(Path.Combine(_savedGames, "Config"));

    public void Dispose() => Directory.Delete(_savedGames, recursive: true);

    [Fact]
    public void NoFile_IsNotEnabled_AndEnableCreatesIt_WithoutABackup()
    {
        Assert.False(AutoexecConfig.IsEnabled(_savedGames));

        AutoexecConfig.Enable(_savedGames);

        Assert.True(AutoexecConfig.IsEnabled(_savedGames));
        Assert.False(File.Exists(ConfigPath + ".bak"));
    }

    [Fact]
    public void Enable_AppendsToTheUsersLists_KeepsEverythingElse_AndBacksUp()
    {
        const string existing = "options.graphics.maxfps = 120\nnet.allow_unsafe_api = { \"gui\" }\n";
        File.WriteAllText(ConfigPath, existing);

        AutoexecConfig.Enable(_savedGames);

        string text = File.ReadAllText(ConfigPath);
        Assert.StartsWith(existing, text);
        // Appends to the existing list instead of replacing it, so "gui" survives.
        Assert.Contains("net.allow_unsafe_api = net.allow_unsafe_api or {}", text);
        Assert.Contains("net.allow_unsafe_api[#net.allow_unsafe_api + 1] = \"userhooks\"", text);
        Assert.Contains("net.allow_dostring_in[#net.allow_dostring_in + 1] = \"mission\"", text);
        Assert.Equal(existing, File.ReadAllText(ConfigPath + ".bak"));
    }

    [Fact]
    public void Enable_Twice_WritesOnce()
    {
        AutoexecConfig.Enable(_savedGames);
        string once = File.ReadAllText(ConfigPath);

        AutoexecConfig.Enable(_savedGames);

        Assert.Equal(once, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ASetupTheUserWroteByHand_CountsAsEnabled()
    {
        File.WriteAllText(ConfigPath, "net.allow_unsafe_api = {\"userhooks\", \"scripting\"}\nnet.allow_dostring_in = {\"mission\"}\n");

        Assert.True(AutoexecConfig.IsEnabled(_savedGames));
    }

    [Fact]
    public void OnlyOneOfTheTwoSettings_IsNotEnabled()
    {
        File.WriteAllText(ConfigPath, "net.allow_unsafe_api = {\"userhooks\"}\n");

        Assert.False(AutoexecConfig.IsEnabled(_savedGames));
    }
}
