using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;

namespace varManager
{
    public static class AccessImport
    {
        private const string PartExtension = ".importing";

        // Fills a new store from an open source database, which it only reads. A store that already exists
        // is left alone, and it returns false. The store appears under its final name only once it is complete.
        public static bool Import(DbConnection source, string storePath)
        {
            if (File.Exists(storePath))
                return false;

            string part = storePath + PartExtension;
            File.Delete(part);
            try
            {
                using (LibraryStore store = LibraryStore.Open(part))
                using (SqliteTransaction transaction = store.Connection.BeginTransaction())
                {
                    foreach (KeyValuePair<string, string[]> table in LibraryStore.Tables)
                        CopyTable(source, store.Connection, transaction, table.Key, table.Value);
                    transaction.Commit();
                }
                File.Move(part, storePath);
            }
            finally
            {
                File.Delete(part);
            }
            return true;
        }

        private static void CopyTable(DbConnection source, SqliteConnection target, SqliteTransaction transaction,
            string table, string[] columns)
        {
            string names = "[" + string.Join("], [", columns) + "]";
            var slots = new string[columns.Length];
            for (int i = 0; i < columns.Length; i++)
                slots[i] = "@p" + i;

            using (DbCommand read = source.CreateCommand())
            using (SqliteCommand write = target.CreateCommand())
            {
                read.CommandText = "SELECT " + names + " FROM [" + table + "]";
                write.Transaction = transaction;
                write.CommandText = "INSERT INTO [" + table + "] (" + names + ") VALUES (" + string.Join(", ", slots) + ")";
                foreach (string slot in slots)
                    write.Parameters.Add(new SqliteParameter { ParameterName = slot });

                using (DbDataReader reader = read.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        for (int i = 0; i < columns.Length; i++)
                            write.Parameters[i].Value = reader.GetValue(i);
                        write.ExecuteNonQuery();
                    }
                }
            }
        }
    }
}
