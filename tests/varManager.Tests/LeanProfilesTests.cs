using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class LeanProfilesTests : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmprofile-" + Guid.NewGuid().ToString("N"));
        private readonly string repo;
        private readonly string links;

        public LeanProfilesTests()
        {
            repo = Path.Combine(root, "repo");
            links = Path.Combine(root, "switch", "___VarsLink___");
            Directory.CreateDirectory(repo);
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private static VarIndex Index(params string[] varNames)
        {
            return new VarIndex(varNames.Select(name =>
            {
                string[] parts = name.Split('.');
                return new VarEntry(name, parts[0], parts[1], int.Parse(parts[2]));
            }));
        }

        private static LeanProfile Profile(string[] direct, params string[] baseVars)
        {
            LeanProfile profile = new LeanProfile();
            profile.Direct.AddRange(direct);
            profile.Base.AddRange(baseVars);
            return profile;
        }

        private string Var(string name)
        {
            string path = Path.Combine(repo, name + ".var");
            File.WriteAllText(path, "content");
            return path;
        }

        private static bool Link(string link, string target)
        {
            if (!CreateSymbolicLink(link, target, 2))
                throw new InvalidOperationException("These tests need Windows Developer Mode to create symlinks.");
            return true;
        }

        private string Target(string name)
        {
            return Path.Combine(repo, name + ".var");
        }

        private ProfilePlan PlanFor(params string[] vars)
        {
            return LeanProfiles.Plan(Profile(vars), Index(vars), v => new string[0], v => 0);
        }

        [Fact]
        public void Plan_includes_direct_base_and_nested_dependencies_and_adds_sizes()
        {
            Dictionary<string, string[]> refs = new Dictionary<string, string[]>
            {
                { "Scene.A.1", new[] { "Look.B.latest" } },
                { "Look.B.2", new string[0] },
                { "Plugin.P.1", new string[0] },
            };
            ProfilePlan plan = LeanProfiles.Plan(Profile(new[] { "Scene.A.1" }, "Plugin.P.latest"),
                Index("Scene.A.1", "Look.B.1", "Look.B.2", "Plugin.P.1"), v => refs[v], v => 10);
            Assert.Equal(new[] { "Look.B.2", "Plugin.P.1", "Scene.A.1" }, plan.Closure.Vars.OrderBy(x => x));
            Assert.Equal(30, plan.Bytes);
        }

        [Fact]
        public void Plan_reports_missing_base_packages_without_failing()
        {
            ProfilePlan plan = LeanProfiles.Plan(Profile(new[] { "A.A.1" }, "Gone.Plugin.latest"), Index("A.A.1"), v => new string[0], v => 0);
            Assert.Equal(new[] { "Gone.Plugin.latest" }, plan.Closure.Missing);
        }

        [Fact]
        public void Diff_ignores_case_and_splits_adds_from_removes()
        {
            ProfileDiff diff = LeanProfiles.Diff(new[] { "A.A.1", "B.B.1" }, new[] { "a.a.1", "C.C.1" });
            Assert.Equal(new[] { "B.B.1" }, diff.Add);
            Assert.Equal(new[] { "C.C.1" }, diff.Remove);
        }

        [Fact]
        public void ApplyLinks_creates_links_and_removes_unwanted_ones()
        {
            Var("A.A.1");
            Var("B.B.1");
            LeanProfiles.ApplyLinks(new[] { "A.A.1", "B.B.1" }, links, Target, Link);
            LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, Target, Link);
            Assert.Equal(new[] { "A.A.1" }, LeanProfiles.Links(links).Select(Path.GetFileNameWithoutExtension));
            Assert.True(File.Exists(Target("B.B.1")));
        }

        [Fact]
        public void ApplyLinks_never_deletes_a_real_file_in_the_folder()
        {
            Directory.CreateDirectory(links);
            string real = Path.Combine(links, "C.C.1.var");
            File.WriteAllText(real, "real");
            Var("A.A.1");
            LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, Target, Link);
            Assert.Equal("real", File.ReadAllText(real));
        }

        [Fact]
        public void ApplyLinks_replaces_a_broken_link()
        {
            string target = Var("A.A.1");
            LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, Target, Link);
            File.Delete(target);
            File.WriteAllText(Path.Combine(repo, "moved.var"), "moved");
            LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, v => Path.Combine(repo, "moved.var"), Link);
            Assert.Equal("moved", File.ReadAllText(Path.Combine(links, "A.A.1.var")));
        }

        [Fact]
        public void ApplyLinks_stops_when_a_link_cannot_be_created()
        {
            Assert.Throws<IOException>(() => LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, Target, (l, t) => false));
        }

        [Fact]
        public void Check_reports_broken_needed_unused_and_missing()
        {
            string broken = Var("A.A.1");
            Var("B.B.1");
            LeanProfiles.ApplyLinks(new[] { "A.A.1", "B.B.1" }, links, Target, Link);
            File.Delete(broken);
            ProfilePlan plan = LeanProfiles.Plan(Profile(new[] { "A.A.1", "C.C.1" }, "Gone.X.latest"),
                Index("A.A.1", "C.C.1"), v => new string[0], v => 0);

            ProfileHealth health = LeanProfiles.Check(plan, links);

            Assert.Equal(new[] { "A.A.1" }, health.BrokenLinks);
            Assert.Equal(new[] { "C.C.1" }, health.NeededLinks);
            Assert.Equal(new[] { "B.B.1" }, health.UnusedLinks);
            Assert.Equal(new[] { "Gone.X.latest" }, health.Missing);
            Assert.True(health.CanRepair);
        }

        [Fact]
        public void Check_of_a_matching_folder_is_healthy()
        {
            Var("A.A.1");
            LeanProfiles.ApplyLinks(new[] { "A.A.1" }, links, Target, Link);
            ProfileHealth health = LeanProfiles.Check(PlanFor("A.A.1"), links);
            Assert.False(health.CanRepair);
            Assert.Contains("healthy", LeanProfiles.Describe(health));
        }

        [Fact]
        public void Check_of_an_absent_folder_needs_every_var()
        {
            Assert.Equal(new[] { "A.A.1" }, LeanProfiles.Check(PlanFor("A.A.1"), links).NeededLinks);
        }

        [Fact]
        public void Save_and_Load_keep_direct_and_base_apart()
        {
            string path = Path.Combine(root, "profiles", "megan.txt");
            LeanProfiles.Save(Profile(new[] { "abc tits.Alice.1" }, "Plugin.P.latest"), path);
            LeanProfile loaded = LeanProfiles.Load(path);
            Assert.Equal(new[] { "abc tits.Alice.1" }, loaded.Direct);
            Assert.Equal(new[] { "Plugin.P.latest" }, loaded.Base);
        }

        [Fact]
        public void Save_keeps_the_prior_version_for_restore()
        {
            string path = Path.Combine(root, "profiles", "megan.txt");
            LeanProfiles.Save(Profile(new[] { "Old.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "New.A.1" }), path);
            Assert.Equal(new[] { "Old.A.1" }, LeanProfiles.Load(LeanProfiles.PreviousPath(path)).Direct);
            Assert.Equal(new[] { "New.A.1" }, LeanProfiles.Load(path).Direct);
        }

        [Fact]
        public void Saving_the_same_picks_keeps_the_real_restore_point()
        {
            string path = Path.Combine(root, "profiles", "megan.profile");
            LeanProfiles.Save(Profile(new[] { "Old.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "New.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "New.A.1" }), path);
            Assert.Equal(new[] { "Old.A.1" }, LeanProfiles.Load(LeanProfiles.PreviousPath(path)).Direct);
        }

        [Fact]
        public void Load_of_an_unrelated_file_has_no_direct_vars()
        {
            string path = Path.Combine(root, "notes.txt");
            File.WriteAllLines(path, new[] { "Some.Var.1", "hello" });
            Assert.Empty(LeanProfiles.Load(path).Direct);
        }

        [Fact]
        public void Missing_packages_alone_are_not_called_healthy()
        {
            ProfileHealth health = new ProfileHealth();
            health.Missing.Add("Gone.X.latest");
            Assert.Contains("Problems found", LeanProfiles.Describe(health));
            Assert.DoesNotContain("Repair makes", LeanProfiles.Describe(health));
        }

        [Fact]
        public void Repairable_problems_warn_about_manual_changes()
        {
            ProfileHealth health = new ProfileHealth();
            health.UnusedLinks.Add("A.A.1");
            Assert.Contains("undoes links you installed", LeanProfiles.Describe(health));
        }

        [Fact]
        public void Describe_plan_shows_counts_and_missing()
        {
            ProfilePlan plan = LeanProfiles.Plan(Profile(new[] { "A.A.1" }, "Gone.X.latest"), Index("A.A.1"), v => new string[0], v => 1073741824);
            string text = LeanProfiles.Describe(plan, LeanProfiles.Diff(plan.Closure.Vars, new string[0]));
            Assert.Matches(@"Vars in this profile: 1 \(1[.,]00 GB\)", text);
            Assert.Contains("Links to add: 1, links to remove: 0", text);
            Assert.Contains("Gone.X.latest", text);
        }
    }
}
