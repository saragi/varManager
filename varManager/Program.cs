using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using varManager.Properties;

namespace varManager
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--undo-migration")
            {
                ShowResult(LibraryMigration.Undo(args[1]), args[1]);
                return;
            }
            if (args.Length == 2 && args[0] == "--rollback-migration")
            {
                RollbackMigration(args[1]);
                return;
            }
            Application.Run(new Form1());
        }

        private static void RollbackMigration(string logPath)
        {
            if (Process.GetProcessesByName("vam").Length > 0)
            {
                MessageBox.Show("Please close VaM first.");
                return;
            }
            string vampath = Settings.Default.vampath;
            if (!File.Exists(Path.Combine(vampath, "VaM.exe")))
            {
                MessageBox.Show("VaM.exe was not found in the configured folder: " + vampath);
                return;
            }
            try
            {
                MigrationUndoResult result = LibraryMigration.Rollback(logPath, Path.Combine(vampath, "AddonPackages"),
                    Path.Combine(vampath, Form1.addonPacksSwitch, "default"),
                    new[] { Form1.installLinkDirName, Form1.missingVarLinkDirName, Form1.tempVarLinkDirName });
                ShowResult(result, logPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Rollback stopped: " + ex.Message + "\r\nIt is safe to run it again.");
            }
        }

        private static void ShowResult(MigrationUndoResult result, string logPath)
        {
            string message = $"Restored {result.Restored} files. Failed: {result.Failed.Count}.";
            if (result.Failed.Count > 0)
            {
                string failedList = logPath + ".failed.txt";
                File.WriteAllLines(failedList, result.Failed);
                message += "\r\nFailed files are listed in: " + failedList;
            }
            string tidied = Path.Combine(Settings.Default.varspath, Form1.tidiedDirName);
            int remaining = Directory.Exists(tidied) ? Directory.GetFiles(tidied, "*.var", SearchOption.AllDirectories).Length : 0;
            if (remaining > 0)
                message += $"\r\n{remaining} vars stay in the repository, because they were not part of this migration.";
            MessageBox.Show(message);
        }
    }
}
