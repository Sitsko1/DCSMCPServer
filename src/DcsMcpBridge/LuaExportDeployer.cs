using System.IO;

/// <summary>
/// Writes the generated Export.lua companion script into a DCS Saved Games folder, and wires it
/// into Export.lua without disturbing whatever other tools (DCS-BIOS, DCSFlightpanels, VAICOM,
/// etc.) already dofile() themselves in there.
/// </summary>
public static class LuaExportDeployer
{
    public const string ExportScriptFileName = "DCSMcpBridgeExport.lua";
    private const string DofileLine = "dofile(lfs.writedir()..[[Scripts\\DCSMcpBridgeExport.lua]])";

    public sealed record DeployResult(bool Success, string Message);

    public static DeployResult Deploy(string savedGamesPath, string dcsHost, int dcsPort)
    {
        try
        {
            string scriptsDir = Path.Combine(savedGamesPath, "Scripts");
            Directory.CreateDirectory(scriptsDir);

            string exportScriptPath = Path.Combine(scriptsDir, ExportScriptFileName);
            File.WriteAllText(exportScriptPath, LuaExportScriptGenerator.Generate(dcsHost, dcsPort));

            string exportLuaPath = Path.Combine(scriptsDir, "Export.lua");
            if (!File.Exists(exportLuaPath))
            {
                File.WriteAllText(exportLuaPath, DofileLine + "\n");
            }
            else if (!File.ReadAllText(exportLuaPath).Contains(ExportScriptFileName))
            {
                File.Copy(exportLuaPath, exportLuaPath + ".bak", overwrite: true);
                File.AppendAllText(exportLuaPath, "\n" + DofileLine + "\n");
            }

            return new DeployResult(true, $"Deployed to {scriptsDir}");
        }
        catch (Exception ex)
        {
            return new DeployResult(false, ex.Message);
        }
    }
}
