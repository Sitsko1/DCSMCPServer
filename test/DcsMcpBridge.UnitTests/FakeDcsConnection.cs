public class FakeDcsConnection : IDcsConnection
{
    public bool ShouldSucceed { get; set; } = true;
    public string? LastLuaCommand { get; private set; }

    public bool SendLuaCommand(string luaCode)
    {
        LastLuaCommand = luaCode;
        return ShouldSucceed;
    }
}
