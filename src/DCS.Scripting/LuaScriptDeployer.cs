using System.IO;
using System.Linq;

namespace DCS.Scripting;

/// <summary>
/// Writes the generated Hooks script into a DCS Saved Games folder (Scripts\Hooks, which DCS
/// loads by itself at startup — no Export.lua edits needed). Also migrates installs made by
/// earlier versions, which loaded an Export.lua companion script: our dofile lines are removed
/// from Export.lua (other tools' lines are left alone, and a .bak is kept) and the old scripts
/// are deleted, since they'd otherwise bind the same port as the Hooks script.
/// </summary>
public static class LuaScriptDeployer
{
    public const string HooksScriptFileName = "DCSMcpBridgeHooks.lua";

    // What earlier versions added to Export.lua, and the scripts those lines loaded.
    private static readonly string[] OldExportDofileLines =
    [
        "dofile(lfs.writedir()..[[Scripts\\DCS.AIAutomator\\DCSMcpBridgeExport.lua]])",
        "dofile(lfs.writedir()..[[Scripts\\DCSMcpBridgeExport.lua]])",
    ];
    private static readonly string[] OldExportScripts =
    [
        Path.Combine("DCS.AIAutomator", "DCSMcpBridgeExport.lua"),
        "DCSMcpBridgeExport.lua",
    ];

    public sealed record DeployResult(bool Success, string Message);

    public static DeployResult Deploy(string savedGamesPath, string dcsHost, int dcsPort)
    {
        if (DcsPathValidator.ValidateSavedGamesPath(savedGamesPath) is { } pathError)
        {
            return new DeployResult(false, pathError);
        }

        try
        {
            string scriptsDir = Path.Combine(savedGamesPath, "Scripts");
            string hooksDir = Path.Combine(scriptsDir, "Hooks");
            string hooksScriptPath = Path.Combine(hooksDir, HooksScriptFileName);

            bool migrated = RemoveOldExportIntegration(scriptsDir);

            string generated = LuaHooksScriptGenerator.Generate(dcsHost, dcsPort);
            if (!migrated && File.Exists(hooksScriptPath) && File.ReadAllText(hooksScriptPath) == generated)
            {
                return new DeployResult(true, "Already deployed - no changes were made.");
            }

            Directory.CreateDirectory(hooksDir);
            File.WriteAllText(hooksScriptPath, generated);
            return new DeployResult(true, $"Deployed to {hooksDir}. Restart DCS to load it.");
        }
        catch (Exception ex)
        {
            return new DeployResult(false, ex.Message);
        }
    }

    /// <summary>Returns true if anything from the old Export.lua integration was removed.</summary>
    private static bool RemoveOldExportIntegration(string scriptsDir)
    {
        bool changed = false;

        string exportLuaPath = Path.Combine(scriptsDir, "Export.lua");
        if (File.Exists(exportLuaPath))
        {
            string exportLua = File.ReadAllText(exportLuaPath);
            string[] lines = exportLua.Split('\n');
            string[] kept = lines.Where(l => !OldExportDofileLines.Contains(l.Trim())).ToArray();
            if (kept.Length != lines.Length)
            {
                File.Copy(exportLuaPath, exportLuaPath + ".bak", overwrite: true);
                File.WriteAllText(exportLuaPath, string.Join('\n', kept));
                changed = true;
            }
        }

        foreach (string oldScript in OldExportScripts.Select(s => Path.Combine(scriptsDir, s)))
        {
            if (File.Exists(oldScript))
            {
                File.Delete(oldScript);
                changed = true;
            }
        }

        string oldAppDir = Path.Combine(scriptsDir, "DCS.AIAutomator");
        if (Directory.Exists(oldAppDir) && !Directory.EnumerateFileSystemEntries(oldAppDir).Any())
        {
            Directory.Delete(oldAppDir);
        }

        return changed;
    }
}
