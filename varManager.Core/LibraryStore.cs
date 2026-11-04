using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace varManager
{
    public sealed class VarRecord
    {
        public string VarName { get; set; }
        public string VarPath { get; set; }
        public string CreatorName { get; set; }
        public string PackageName { get; set; }
        public int? Version { get; set; }
        public DateTime? MetaDate { get; set; }
        public DateTime? VarDate { get; set; }
        public double? FileSize { get; set; }
        public int? Scenes { get; set; }
        public int? Looks { get; set; }
        public int? Clothing { get; set; }
        public int? Hairstyle { get; set; }
        public int? Plugins { get; set; }
        public int? Assets { get; set; }
        public int? Morphs { get; set; }
        public int? Pose { get; set; }
        public int? Skin { get; set; }
        public bool Installed { get; set; }
        public bool Disabled { get; set; }
    }

    public sealed class ScenePreview
    {
        public string VarName { get; set; }
        public string Type { get; set; }
        public string ScenePath { get; set; }
        public string Picture { get; set; }
    }

    // The new app's database. It keeps the Access table and column names, and var names ignore case as they do there.
    public sealed class LibraryStore : IDisposable
    {
        public const int SchemaVersion = 1;

        internal static readonly IReadOnlyDictionary<string, string[]> Tables = new Dictionary<string, string[]>
        {
            ["vars"] = new[]
            {
                "varName", "varPath", "creatorName", "packageName", "version", "metaDate", "varDate", "filesize",
                "scenes", "looks", "clothing", "hairstyle", "plugins", "assets", "morphs", "pose", "skin"
            },
            ["installStatus"] = new[] { "varName", "Installed", "Disabled" },
            ["dependencies"] = new[] { "ID", "varName", "dependency" },
            ["scenes"] = new[] { "ID", "varName", "atomType", "isPreset", "scenePath", "previewPic" },
            ["savedepens"] = new[] { "ID", "savepath", "modidate", "dependency" },
        };

        private const string Schema = @"
CREATE TABLE vars (
    varName TEXT PRIMARY KEY COLLATE NOCASE, varPath TEXT, creatorName TEXT, packageName TEXT, version INTEGER,
    metaDate TEXT, varDate TEXT, filesize REAL, scenes INTEGER, looks INTEGER, clothing INTEGER, hairstyle INTEGER,
    plugins INTEGER, assets INTEGER, morphs INTEGER, pose INTEGER, skin INTEGER);
CREATE TABLE installStatus (varName TEXT PRIMARY KEY COLLATE NOCASE, Installed INTEGER, Disabled INTEGER);
CREATE TABLE dependencies (ID INTEGER PRIMARY KEY, varName TEXT COLLATE NOCASE, dependency TEXT COLLATE NOCASE);
CREATE TABLE scenes (
    ID INTEGER PRIMARY KEY, varName TEXT COLLATE NOCASE, atomType TEXT, isPreset INTEGER, scenePath TEXT, previewPic TEXT);
CREATE TABLE savedepens (ID INTEGER PRIMARY KEY, savepath TEXT, modidate TEXT, dependency TEXT COLLATE NOCASE);
CREATE INDEX dependencies_varName ON dependencies (varName);
CREATE INDEX dependencies_dependency ON dependencies (dependency);
CREATE INDEX scenes_varName ON scenes (varName);
CREATE INDEX savedepens_savepath ON savedepens (savepath);
CREATE INDEX savedepens_dependency ON savedepens (dependency);";

        private readonly SqliteConnection connection;

        private LibraryStore(SqliteConnection connection)
        {
            this.connection = connection;
        }

        internal SqliteConnection Connection => connection;

        public static LibraryStore Open(string path)
        {
            // Pooling would keep the file locked after Dispose, and the import has to rename it.
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();
                int version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"));
                if (version > SchemaVersion)
                    throw new InvalidDataException(
                        "This database was made by a newer version of varManager (schema " + version + ").");
                if (version == 0)
                    CreateSchema(connection);
                return new LibraryStore(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public long Count(string table)
        {
            if (!Tables.ContainsKey(table))
                throw new ArgumentException("Unknown table: " + table, nameof(table));
            return Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM [" + table + "]"));
        }

        public IReadOnlyList<VarRecord> LoadVars()
        {
            var vars = new List<VarRecord>();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    @"SELECT v.varName, v.varPath, v.creatorName, v.packageName, v.version, v.metaDate, v.varDate,
                             v.filesize, v.scenes, v.looks, v.clothing, v.hairstyle, v.plugins, v.assets, v.morphs,
                             v.pose, v.skin, s.Installed, s.Disabled
                      FROM vars v LEFT JOIN installStatus s ON s.varName = v.varName
                      ORDER BY v.varName";
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        vars.Add(new VarRecord
                        {
                            VarName = reader.GetString(0),
                            VarPath = Text(reader, 1),
                            CreatorName = Text(reader, 2),
                            PackageName = Text(reader, 3),
                            Version = Number(reader, 4),
                            MetaDate = Date(reader, 5),
                            VarDate = Date(reader, 6),
                            FileSize = reader.IsDBNull(7) ? (double?)null : reader.GetDouble(7),
                            Scenes = Number(reader, 8),
                            Looks = Number(reader, 9),
                            Clothing = Number(reader, 10),
                            Hairstyle = Number(reader, 11),
                            Plugins = Number(reader, 12),
                            Assets = Number(reader, 13),
                            Morphs = Number(reader, 14),
                            Pose = Number(reader, 15),
                            Skin = Number(reader, 16),
                            Installed = Flag(reader, 17),
                            Disabled = Flag(reader, 18),
                        });
                    }
                }
            }
            return vars;
        }

        public List<KeyValuePair<string, string>> LoadDependencies()
        {
            var dependencies = new List<KeyValuePair<string, string>>();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT varName, dependency FROM dependencies WHERE varName IS NOT NULL AND dependency IS NOT NULL";
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        dependencies.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
                }
            }
            return dependencies;
        }

        public List<ScenePreview> LoadPreviews()
        {
            return ReadItems("SELECT varName, atomType, scenePath, previewPic FROM scenes WHERE previewPic IS NOT NULL AND previewPic <> ''");
        }

        /// <summary>Every scene and preset, with or without a picture.</summary>
        public List<ScenePreview> LoadItems()
        {
            return ReadItems("SELECT varName, atomType, scenePath, previewPic FROM scenes WHERE varName IS NOT NULL AND scenePath IS NOT NULL ORDER BY varName, ID");
        }

        /// <summary>Replaces a var's record with a fresh scan, all or nothing.</summary>
        public void SaveVar(string varName, string varPath, VarScan scan)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                DeleteRecords(transaction, varName);
                Execute(transaction,
                    @"INSERT INTO vars (varName, varPath, creatorName, packageName, version, metaDate, varDate, filesize,
                                        scenes, looks, clothing, hairstyle, plugins, assets, morphs, pose, skin)
                      VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16)",
                    varName, varPath, scan.CreatorName, scan.PackageName, scan.Version, scan.MetaDate, scan.VarDate, (double)scan.FileSize,
                    scan.Scenes, scan.Looks, scan.Clothing, scan.Hairstyle, scan.Plugins, scan.Assets, scan.Morphs, scan.Pose, scan.Skin);
                foreach (VarItem item in scan.Items)
                {
                    Execute(transaction,
                        "INSERT INTO scenes (varName, atomType, isPreset, scenePath, previewPic) VALUES (@p0, @p1, @p2, @p3, @p4)",
                        varName, item.Type, item.IsPreset, item.EntryName, item.Preview);
                }
                foreach (string dependency in scan.Dependencies)
                    Execute(transaction, "INSERT INTO dependencies (varName, dependency) VALUES (@p0, @p1)", varName, dependency);
                transaction.Commit();
            }
        }

        public void UpdateVarPath(string varName, string varPath)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                Execute(transaction, "UPDATE vars SET varPath = @p0 WHERE varName = @p1", varPath, varName);
                transaction.Commit();
            }
        }

        public void DeleteVars(IEnumerable<string> varNames)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                foreach (string varName in varNames)
                    DeleteRecords(transaction, varName);
                transaction.Commit();
            }
        }

        /// <summary>Replaces the install status of every var. The value says whether the link is disabled.</summary>
        public void SetInstallStatus(IEnumerable<KeyValuePair<string, bool>> linked)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                Execute(transaction, "DELETE FROM installStatus");
                foreach (KeyValuePair<string, bool> link in linked)
                    Execute(transaction, "INSERT INTO installStatus (varName, Installed, Disabled) VALUES (@p0, 1, @p1)", link.Key, link.Value);
                transaction.Commit();
            }
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        private List<ScenePreview> ReadItems(string sql)
        {
            var items = new List<ScenePreview>();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(new ScenePreview
                        {
                            VarName = reader.GetString(0),
                            Type = Text(reader, 1),
                            ScenePath = reader.GetString(2),
                            Picture = Text(reader, 3),
                        });
                    }
                }
            }
            return items;
        }

        private void DeleteRecords(SqliteTransaction transaction, string varName)
        {
            foreach (string table in new[] { "dependencies", "scenes", "vars" })
                Execute(transaction, "DELETE FROM [" + table + "] WHERE varName = @p0", varName);
        }

        private void Execute(SqliteTransaction transaction, string sql, params object[] values)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                for (int i = 0; i < values.Length; i++)
                    command.Parameters.AddWithValue("@p" + i, values[i] ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            // Takes the write lock first, then looks again: on a new file a background read and
            // Update DB can both get here, and only one of them may make the tables.
            using (SqliteTransaction transaction = connection.BeginTransaction(false))
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                    return;
                command.CommandText = Schema + "PRAGMA user_version = " + SchemaVersion + ";";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static object Scalar(SqliteConnection connection, string sql)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }

        private static string Text(SqliteDataReader reader, int column)
        {
            return reader.IsDBNull(column) ? null : reader.GetString(column);
        }

        private static int? Number(SqliteDataReader reader, int column)
        {
            return reader.IsDBNull(column) ? (int?)null : reader.GetInt32(column);
        }

        private static DateTime? Date(SqliteDataReader reader, int column)
        {
            return reader.IsDBNull(column) ? (DateTime?)null : reader.GetDateTime(column);
        }

        private static bool Flag(SqliteDataReader reader, int column)
        {
            return !reader.IsDBNull(column) && reader.GetBoolean(column);
        }
    }
}
