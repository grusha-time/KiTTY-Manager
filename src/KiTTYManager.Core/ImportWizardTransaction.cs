namespace KiTTYManager.Core;

public static class ImportWizardTransaction
{
    public static string SaveWithBackup(string configPath, ManagerConfig merged)
    {
        var fullPath = Path.GetFullPath(configPath);
        var backupPath = fullPath + ".before-import-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".bak";
        if (File.Exists(fullPath)) File.Copy(fullPath, backupPath, overwrite: false);
        try
        {
            ConfigStore.Save(fullPath, merged);
            return backupPath;
        }
        catch
        {
            if (File.Exists(backupPath)) File.Copy(backupPath, fullPath, overwrite: true);
            throw;
        }
    }

    public static void Rollback(string configPath, string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup импорта не найден.", backupPath);
        File.Copy(backupPath, Path.GetFullPath(configPath), overwrite: true);
    }
}
