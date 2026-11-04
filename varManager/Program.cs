using System;
using System.Windows.Forms;

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
                MigrationUndoResult result = LibraryMigration.Undo(args[1]);
                MessageBox.Show($"Restored {result.Restored} files. Failed: {result.Failed.Count}.\r\n"
                    + string.Join("\r\n", result.Failed));
                return;
            }
            Application.Run(new Form1());
        }
    }
}
