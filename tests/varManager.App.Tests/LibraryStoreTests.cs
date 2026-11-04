using System;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class LibraryStoreTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "varManager-store-" + Guid.NewGuid().ToString("N"));
        private readonly string mdb;
        private readonly string store;

        public LibraryStoreTests()
        {
            Directory.CreateDirectory(folder);
            mdb = Path.Combine(folder, "varManager.mdb");
            store = Path.Combine(folder, "library.db");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "template.mdb"), mdb);
        }

        public void Dispose()
        {
            Directory.Delete(folder, true);
        }

        private void Exec(params string[] statements)
        {
            using (var connection = new OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=" + mdb))
            {
                connection.Open();
                foreach (string sql in statements)
                {
                    using (OleDbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        command.ExecuteNonQuery();
                    }
                }
            }
            OleDbConnection.ReleaseObjectPool();
        }

        private void Seed()
        {
            Exec(
                "INSERT INTO vars (varName, varPath, creatorName, packageName, version, metaDate, varDate, filesize, scenes, skin) " +
                "VALUES ('Deep Creator.Pack_é.3', 'x\\y', 'Deep Creator', 'Pack_é', 3, #2024-02-03 04:05:06#, #2024-03-04 05:06:07#, 1234.5, 2, 1)",
                "INSERT INTO vars (varName, filesize) VALUES ('Bare.Pack.1', NULL)",
                "INSERT INTO vars (varName) VALUES ('Mixed.Case.1')",
                "INSERT INTO vars (varName) VALUES ('Off.Pack.1')",
                "INSERT INTO installStatus (varName, Installed, Disabled) VALUES ('Deep Creator.Pack_é.3', True, True)",
                "INSERT INTO installStatus (varName, Installed, Disabled) VALUES ('MIXED.CASE.1', True, False)",
                "INSERT INTO installStatus (varName, Installed, Disabled) VALUES ('Off.Pack.1', False, False)",
                "INSERT INTO dependencies (varName, dependency) VALUES ('Deep Creator.Pack_é.3', 'Bare.Pack.latest')",
                "INSERT INTO scenes (varName, atomType, isPreset, scenePath, previewPic) VALUES ('Bare.Pack.1', 'Person', True, 'Saves/scene/a.json', 'a.jpg')",
                "INSERT INTO savedepens (savepath, modidate, dependency) VALUES ('Saves/b.json', #2024-05-06#, 'Bare.Pack.1')");
        }

        private string[] Row(string sql)
        {
            using (var connection = new SqliteConnection("Data Source=" + store + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    using (SqliteDataReader reader = command.ExecuteReader())
                    {
                        Assert.True(reader.Read());
                        return Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString()).ToArray();
                    }
                }
            }
        }

        private static string[] TempCopies()
        {
            return Directory.GetFiles(Path.GetTempPath(), "varManager-import-*");
        }

        private static byte[] Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return sha.ComputeHash(file);
        }

        [Fact]
        public void Import_copies_every_table()
        {
            Seed();

            Assert.True(AccessDatabase.ImportOnce(mdb, store));

            using (LibraryStore library = LibraryStore.Open(store))
            {
                Assert.Equal(4, library.Count("vars"));
                Assert.Equal(3, library.Count("installStatus"));
                Assert.Equal(1, library.Count("dependencies"));
                Assert.Equal(1, library.Count("scenes"));
                Assert.Equal(1, library.Count("savedepens"));
            }
        }

        [Fact]
        public void Import_keeps_values_and_nulls()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);

            using (LibraryStore library = LibraryStore.Open(store))
            {
                var vars = library.LoadVars();
                VarRecord full = vars.Single(v => v.VarName == "Deep Creator.Pack_é.3");
                Assert.Equal("x\\y", full.VarPath);
                Assert.Equal("Deep Creator", full.CreatorName);
                Assert.Equal(3, full.Version);
                Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6), full.MetaDate);
                Assert.Equal(new DateTime(2024, 3, 4, 5, 6, 7), full.VarDate);
                Assert.Equal(1234.5, full.FileSize);
                Assert.Equal(2, full.Scenes);
                Assert.Equal(1, full.Skin);
                Assert.True(full.Installed);
                Assert.True(full.Disabled);

                VarRecord bare = vars.Single(v => v.VarName == "Bare.Pack.1");
                Assert.Null(bare.VarPath);
                Assert.Null(bare.MetaDate);
                Assert.Null(bare.FileSize);
                Assert.False(bare.Installed);
            }
        }

        [Fact]
        public void Import_keeps_the_other_tables_columns()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);

            Assert.Equal(new[] { "Deep Creator.Pack_é.3", "Bare.Pack.latest" }, Row("SELECT varName, dependency FROM dependencies"));
            Assert.Equal(new[] { "Bare.Pack.1", "Person", "1", "Saves/scene/a.json", "a.jpg" },
                Row("SELECT varName, atomType, isPreset, scenePath, previewPic FROM scenes"));
            Assert.Equal(new[] { "Saves/b.json", "Bare.Pack.1" }, Row("SELECT savepath, dependency FROM savedepens"));
            Assert.StartsWith("2024-05-06", Row("SELECT modidate FROM savedepens")[0]);
        }

        [Fact]
        public void Install_status_joins_whatever_the_case_and_keeps_false_flags()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);

            using (LibraryStore library = LibraryStore.Open(store))
            {
                var vars = library.LoadVars();
                VarRecord mixed = vars.Single(v => v.VarName == "Mixed.Case.1");
                Assert.True(mixed.Installed);
                Assert.False(mixed.Disabled);
                VarRecord off = vars.Single(v => v.VarName == "Off.Pack.1");
                Assert.False(off.Installed);
                Assert.False(off.Disabled);
            }
        }

        [Fact]
        public void Import_reads_a_read_only_access_file_and_cleans_up()
        {
            Seed();
            string[] before = TempCopies();
            File.SetAttributes(mdb, FileAttributes.ReadOnly);
            try
            {
                Assert.True(AccessDatabase.ImportOnce(mdb, store));
            }
            finally
            {
                File.SetAttributes(mdb, FileAttributes.Normal);
            }

            Assert.Equal(before, TempCopies());
        }

        [Fact]
        public void Import_leaves_the_access_file_untouched()
        {
            Seed();
            byte[] before = Hash(mdb);
            DateTime written = File.GetLastWriteTimeUtc(mdb);

            AccessDatabase.ImportOnce(mdb, store);

            Assert.Equal(before, Hash(mdb));
            Assert.Equal(written, File.GetLastWriteTimeUtc(mdb));
            Assert.Equal(new[] { "library.db", "varManager.mdb" },
                Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void Import_runs_only_once()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);
            Exec("INSERT INTO vars (varName) VALUES ('Later.Pack.1')");

            Assert.False(AccessDatabase.ImportOnce(mdb, store));

            using (LibraryStore library = LibraryStore.Open(store))
                Assert.Equal(4, library.Count("vars"));
        }

        [Fact]
        public void Failed_import_leaves_no_store()
        {
            Seed();
            Exec("DROP TABLE savedepens");
            string[] before = TempCopies();

            Assert.Throws<OleDbException>(() => AccessDatabase.ImportOnce(mdb, store));

            Assert.Equal(new[] { "varManager.mdb" }, Directory.GetFiles(folder).Select(Path.GetFileName));
            Assert.Equal(before, TempCopies());
        }

        [Fact]
        public void Import_replaces_a_stale_partial_file()
        {
            Seed();
            File.WriteAllText(store + ".importing", "left by a crash");

            Assert.True(AccessDatabase.ImportOnce(mdb, store));

            using (LibraryStore library = LibraryStore.Open(store))
                Assert.Equal(4, library.Count("vars"));
            Assert.False(File.Exists(store + ".importing"));
        }

        [Fact]
        public void Var_names_ignore_case()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);

            using (var connection = new SqliteConnection("Data Source=" + store + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM vars WHERE varName = 'BARE.PACK.1'";
                    Assert.Equal(1L, command.ExecuteScalar());
                }
            }
        }

        [Fact]
        public void Open_keeps_existing_data()
        {
            Seed();
            AccessDatabase.ImportOnce(mdb, store);

            using (LibraryStore.Open(store)) { }

            using (LibraryStore library = LibraryStore.Open(store))
                Assert.Equal(4, library.Count("vars"));
        }

        [Fact]
        public void Open_refuses_a_newer_schema()
        {
            using (var connection = new SqliteConnection("Data Source=" + store + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version = " + (LibraryStore.SchemaVersion + 1);
                    command.ExecuteNonQuery();
                }
            }

            Assert.Throws<InvalidDataException>(() => LibraryStore.Open(store));
        }

        [Fact]
        public void Count_refuses_an_unknown_table()
        {
            using (LibraryStore library = LibraryStore.Open(store))
                Assert.Throws<ArgumentException>(() => library.Count("sqlite_master"));
        }
    }
}
