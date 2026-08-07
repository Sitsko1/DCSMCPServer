/// <summary>
/// The DCS-facing side of a tool: send raw Lua to be executed inside DCS World.
/// Exists so tool classes (e.g. AtcTools) can be unit tested against a fake, without a real
/// DCS instance or TCP socket.
/// </summary>
public interface IDcsConnection
{
    /// <summary>
    /// Directly pushes raw Lua code over the socket to be executed inside DCS.
    /// Returns false if there is no live connection or the write fails.
    /// </summary>
    bool SendLuaCommand(string luaCode);
}
