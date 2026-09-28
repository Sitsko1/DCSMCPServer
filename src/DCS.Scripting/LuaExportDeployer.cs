using System.IO;

namespace DCS.Scripting;

/// <summary>
/// Writes the generated Export.lua companion script into a DCS Saved Games folder, and wires it
/// into Export.lua without disturbing whatever other tools (DCS-BIOS, DCSFlightpanels, VAICOM,
/// etc.) already dofile() themselves in there. Companion scripts live in a per-app subdirectory
/// under Scripts (see <see cref="AppSubdirectoryName"/>), so future files this app adds never
/// collide with other tools' files.
/// </summary>
public static class LuaExportDeployer
{
    public const string ExportScriptFileName = "DCSMcpBridgeExport.lua";
    public const string AppSubdirectoryName = "DCS.AIAutomator";
    private const string DofileLine = "dofile(lfs.writedir()..[[Scripts\\DCS.AIAutomator\\DCSMcpBridgeExport.lua]])";
    // Written by versions before the per-app subdirectory; rewritten to DofileLine on deploy.
    private const string LegacyDofileLine = "dofile(lfs.writedir()..[[Scripts\\DCSMcpBridgeExport.lua]])";

    public sealed record DeployResult(bool Success, string Message);

    public static DeployResult Deploy(string savedGamesPath, string dcsHost, int dcsPort)
    {
        try
        {
            string scriptsDir = Path.Combine(savedGamesPath, "Scripts");
            string appDir = Path.Combine(scriptsDir, AppSubdirectoryName);
            string exportScriptPath = Path.Combine(appDir, ExportScriptFileName);
            string exportLuaPath = Path.Combine(scriptsDir, "Export.lua");

            string generated = LuaExportScriptGenerator.Generate(dcsHost, dcsPort);

            // 1. Does Export.lua exist, and is it already wired to our companion script?
            bool exportLuaExists = File.Exists(exportLuaPath);
            string exportLua = exportLuaExists ? File.ReadAllText(exportLuaPath) : "";
            bool alreadyWired = exportLua.Contains(DofileLine);

            if (alreadyWired)
            {
                // 2. Verify the companion script exists in the right location and matches what we'd deploy.
                bool scriptExists = File.Exists(exportScriptPath);
                bool scriptIdentical = scriptExists && File.ReadAllText(exportScriptPath) == generated;

                if (scriptExists && scriptIdentical)
                {
                    return new DeployResult(true, "Already deployed - no changes were made.");
                }
            }

            // 3. Otherwise write all files.
            Directory.CreateDirectory(appDir);
            File.WriteAllText(exportScriptPath, generated);

            if (!exportLuaExists)
            {
                File.WriteAllText(exportLuaPath, DofileLine + "\n");
            }
            else if (!alreadyWired)
            {
                File.Copy(exportLuaPath, exportLuaPath + ".bak", overwrite: true);
                if (exportLua.Contains(LegacyDofileLine))
                {
                    // Replace rather than append, or both scripts would run and fight over the port.
                    File.WriteAllText(exportLuaPath, exportLua.Replace(LegacyDofileLine, DofileLine));
                }
                else
                {
                    File.AppendAllText(exportLuaPath, "\n" + DofileLine + "\n");
                }
            }

            return new DeployResult(true, $"Deployed to {appDir}");
        }
        catch (Exception ex)
        {
            return new DeployResult(false, ex.Message);
        }
    }
}
