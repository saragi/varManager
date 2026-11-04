using System;
using System.Data.OleDb;
using System.IO;

namespace varManager.Ui
{
    public static class AccessDatabase
    {
        // Works on a temporary copy, so the old app's database is never opened and gains no lock file.
        public static bool ImportOnce(string mdbPath, string storePath)
        {
            if (File.Exists(storePath))
                return false;

            string copy = Path.Combine(Path.GetTempPath(), "varManager-import-" + Guid.NewGuid().ToString("N") + ".mdb");
            File.Copy(mdbPath, copy);
            try
            {
                File.SetAttributes(copy, FileAttributes.Normal);
                var builder = new OleDbConnectionStringBuilder
                {
                    Provider = "Microsoft.ACE.OLEDB.12.0",
                    DataSource = copy,
                };
                builder["Mode"] = "Read";
                using var connection = new OleDbConnection(builder.ConnectionString);
                connection.Open();
                return AccessImport.Import(connection, storePath);
            }
            finally
            {
                OleDbConnection.ReleaseObjectPool();
                File.Delete(copy);
                File.Delete(Path.ChangeExtension(copy, ".ldb"));
            }
        }
    }
}
