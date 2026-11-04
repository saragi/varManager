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
                ShowResult(LibraryMigration.Undo(args[1]));
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
            try
            {
                ShowResult(LibraryMigration.Rollback(logPath, Path.Combine(vampath, "AddonPackages"),
                    Path.Combine(vampath, Form1.addonPacksSwitch, "default"),
                    new[] { Form1.installLinkDirName, Form1.missingVarLinkDirName, Form1.tempVarLinkDirName }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show("Rollback stopped: " + ex.Message);
            }
        }

        private static void ShowResult(MigrationUndoResult result)
        {
            MessageBox.Show($"Restored {result.Restored} files. Failed: {result.Failed.Count}.\r\n"
                + string.Join("\r\n", result.Failed));
        }
    }
}
