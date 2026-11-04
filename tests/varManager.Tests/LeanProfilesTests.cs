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
        public void Saving_the_same_picks_in_another_order_keeps_the_restore_point()
        {
            string path = Path.Combine(root, "profiles", "megan.profile");
            LeanProfiles.Save(Profile(new[] { "Old.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "B.B.1", "A.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "A.A.1", "B.B.1" }), path);
            Assert.Equal(new[] { "Old.A.1" }, LeanProfiles.Load(LeanProfiles.PreviousPath(path)).Direct);
        }

        [Fact]
        public void DeleteFiles_is_safe_when_the_folder_does_not_exist()
        {
            LeanProfiles.DeleteFiles(Path.Combine(root, "no-such-folder", "a.profile"));
        }

        [Fact]
        public void DeleteFiles_removes_the_profile_and_prior_version()
        {
            string path = Path.Combine(root, "profiles", "a.profile");
            LeanProfiles.Save(Profile(new[] { "A.A.1" }), path);
            LeanProfiles.Save(Profile(new[] { "B.B.1" }), path);
            LeanProfiles.DeleteFiles(path);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(LeanProfiles.PreviousPath(path)));
        }

        [Fact]
        public void RenameFiles_moves_both_files_and_replaces_leftovers()
        {
            string oldPath = Path.Combine(root, "profiles", "old.profile");
            string newPath = Path.Combine(root, "profiles", "new.profile");
            LeanProfiles.Save(Profile(new[] { "A.A.1" }), oldPath);
            LeanProfiles.Save(Profile(new[] { "B.B.1" }), oldPath);
            LeanProfiles.Save(Profile(new[] { "Leftover.L.1" }), newPath);
            LeanProfiles.Save(Profile(new[] { "Leftover.L.2" }), newPath);
            LeanProfiles.RenameFiles(oldPath, newPath);
            Assert.False(File.Exists(oldPath));
            Assert.Equal(new[] { "B.B.1" }, LeanProfiles.Load(newPath).Direct);
            Assert.Equal(new[] { "A.A.1" }, LeanProfiles.Load(LeanProfiles.PreviousPath(newPath)).Direct);
        }

        [Fact]
        public void RenameFiles_of_a_plain_switch_creates_nothing()
        {
            string folder = Path.Combine(root, "none");
            LeanProfiles.RenameFiles(Path.Combine(folder, "a.profile"), Path.Combine(folder, "b.profile"));
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void RenameFiles_clears_stale_files_at_the_new_name()
        {
            string newPath = Path.Combine(root, "profiles", "new.profile");
            LeanProfiles.Save(Profile(new[] { "Stale.S.1" }), newPath);
            LeanProfiles.RenameFiles(Path.Combine(root, "profiles", "plain.profile"), newPath);
            Assert.False(File.Exists(newPath));
        }

        [Fact]
        public void RenameFiles_moves_a_lone_prior_version()
        {
            string oldPath = Path.Combine(root, "profiles", "old.profile");
            string newPath = Path.Combine(root, "profiles", "new.profile");
            LeanProfiles.Save(Profile(new[] { "A.A.1" }), oldPath);
            LeanProfiles.Save(Profile(new[] { "B.B.1" }), oldPath);
            File.Delete(oldPath);
            LeanProfiles.RenameFiles(oldPath, newPath);
            Assert.True(File.Exists(LeanProfiles.PreviousPath(newPath)));
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

        private static ProfilePlan Excluding(string[] exclude, string[] linked, Dictionary<string, string[]> references,
            string[] library = null, string[] baseVars = null)
        {
            LeanProfile profile = new LeanProfile();
            profile.Exclude.AddRange(exclude);
            profile.Base.AddRange(baseVars ?? new string[0]);
            Dictionary<string, string[]> anyCase = new Dictionary<string, string[]>(references, StringComparer.OrdinalIgnoreCase);
            return LeanProfiles.PlanExcluding(profile, linked, Index(library ?? linked),
                name => anyCase.TryGetValue(name, out string[] found) ? found : new string[0], name => 1);
        }

        [Fact]
        public void Excluding_links_everything_else()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "B.Other.1" }, new Dictionary<string, string[]>());

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "A.Scene.1" }, plan.LeftOut);
        }

        [Fact]
        public void Excluding_also_drops_dependencies_only_it_uses()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "A.Hair.1", "A.Inner.1", "B.Other.1" },
                new Dictionary<string, string[]>
                {
                    ["A.Scene.1"] = new[] { "A.Hair.1" },
                    ["A.Hair.1"] = new[] { "A.Inner.1" }
                });

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "A.Hair.1", "A.Inner.1" }, plan.AlsoLeftOut);
        }

        [Fact]
        public void Excluding_keeps_a_dependency_another_var_uses()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "A.Hair.1", "B.Other.1" },
                new Dictionary<string, string[]>
                {
                    ["A.Scene.1"] = new[] { "A.Hair.1" },
                    ["B.Other.1"] = new[] { "A.Hair.1" }
                });

            Assert.Equal(new[] { "A.Hair.1", "B.Other.1" }, plan.Closure.Vars);
            Assert.Empty(plan.AlsoLeftOut);
        }

        [Fact]
        public void Excluding_keeps_a_var_something_needs_and_says_what()
        {
            ProfilePlan plan = Excluding(new[] { "A.Plugin.1" }, new[] { "A.Plugin.1", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Plugin.1" } });

            Assert.Contains("A.Plugin.1", plan.Closure.Vars);
            Assert.Empty(plan.LeftOut);
            Assert.Equal(new[] { "A.Plugin.1 needed by B.Scene.1" }, plan.StillNeeded);
        }

        [Fact]
        public void Excluding_never_removes_the_base_set()
        {
            ProfilePlan plan = Excluding(new[] { "A.Session.1" }, new[] { "A.Session.1", "A.Dep.1" },
                new Dictionary<string, string[]> { ["A.Session.1"] = new[] { "A.Dep.1" } }, baseVars: new[] { "A.Session.latest" });

            Assert.Equal(new[] { "A.Dep.1", "A.Session.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "A.Session.1 needed by the base set" }, plan.StillNeeded);
        }

        [Fact]
        public void Excluding_leaves_disabled_vars_off()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "B.Other.1" },
                new Dictionary<string, string[]> { ["B.Other.1"] = new[] { "C.Disabled.1" } },
                new[] { "A.Scene.1", "B.Other.1", "C.Disabled.1" });

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
            Assert.Empty(plan.Closure.Missing);
        }

        [Fact]
        public void Excluding_reports_an_exclusion_default_does_not_link()
        {
            ProfilePlan plan = Excluding(new[] { "C.Disabled.1" }, new[] { "B.Other.1" }, new Dictionary<string, string[]>(),
                new[] { "B.Other.1", "C.Disabled.1" });

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "C.Disabled.1" }, plan.AlreadyOff);
        }

        [Fact]
        public void Excluding_resolves_latest_among_linked_versions()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.1" }, new[] { "A.Hair.1", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.latest" } },
                new[] { "A.Hair.1", "A.Hair.2", "B.Scene.1" });

            Assert.Equal(new[] { "A.Hair.1", "B.Scene.1" }, plan.Closure.Vars);
        }

        [Fact]
        public void Excluding_handles_dependency_loops()
        {
            ProfilePlan plan = Excluding(new[] { "A.One.1" }, new[] { "A.One.1", "A.Two.1", "B.Other.1" },
                new Dictionary<string, string[]>
                {
                    ["A.One.1"] = new[] { "A.Two.1" },
                    ["A.Two.1"] = new[] { "A.One.1" }
                });

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
        }

        [Fact]
        public void Excluding_reports_packages_nothing_has()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "B.Other.1" },
                new Dictionary<string, string[]> { ["B.Other.1"] = new[] { "Gone.Pack.1" } });

            Assert.Equal(new[] { "Gone.Pack.1" }, plan.Closure.Missing);
        }

        [Fact]
        public void Exclusions_survive_save_and_load()
        {
            string path = Path.Combine(root, "trim.profile");
            LeanProfile profile = new LeanProfile();
            profile.Base.Add("A.Base.1");
            profile.Exclude.Add("Spaced Creator.Scene.1");

            LeanProfiles.Save(profile, path);
            LeanProfile loaded = LeanProfiles.Load(path);

            Assert.Equal(new[] { "Spaced Creator.Scene.1" }, loaded.Exclude);
            Assert.Equal(new[] { "A.Base.1" }, loaded.Base);
            Assert.Empty(loaded.Direct);
        }

        [Fact]
        public void Describe_lists_what_an_exclusion_changes_but_not_every_var()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1", "A.Plugin.1" }, new[] { "A.Scene.1", "A.Hair.1", "A.Plugin.1", "B.Other.1" },
                new Dictionary<string, string[]>
                {
                    ["A.Scene.1"] = new[] { "A.Hair.1" },
                    ["B.Other.1"] = new[] { "A.Plugin.1" }
                });

            string text = LeanProfiles.Describe(plan, new ProfileDiff());

            Assert.Contains("Left out:", text);
            Assert.Contains("Also left out", text);
            Assert.Contains("A.Plugin.1 needed by B.Other.1", text);
            Assert.DoesNotContain("Vars:", text);
        }

        [Fact]
        public void Excluding_matches_names_in_any_case()
        {
            ProfilePlan plan = Excluding(new[] { "a.scene.1" }, new[] { "A.Scene.1", "B.Other.1" }, new Dictionary<string, string[]>());

            Assert.Equal(new[] { "B.Other.1" }, plan.Closure.Vars);
            Assert.Empty(plan.AlreadyOff);
        }

        [Fact]
        public void Excluding_keeps_everything_a_kept_exclusion_needs()
        {
            ProfilePlan plan = Excluding(new[] { "A.X.1" }, new[] { "A.X.1", "A.D.1", "A.E.1", "B.Y.1" },
                new Dictionary<string, string[]>
                {
                    ["B.Y.1"] = new[] { "A.X.1" },
                    ["A.X.1"] = new[] { "A.D.1" },
                    ["A.D.1"] = new[] { "A.E.1" }
                });

            Assert.Equal(new[] { "A.D.1", "A.E.1", "A.X.1", "B.Y.1" }, plan.Closure.Vars);
            Assert.Empty(plan.AlsoLeftOut);
        }

        [Fact]
        public void Excluding_counts_a_substitute_version_as_used()
        {
            ProfilePlan plan = Excluding(new[] { "C.Scene.1" }, new[] { "A.Hair.1", "B.Scene.1", "C.Scene.1" },
                new Dictionary<string, string[]>
                {
                    ["B.Scene.1"] = new[] { "A.Hair.2" },
                    ["C.Scene.1"] = new[] { "A.Hair.1" }
                });

            Assert.Contains("A.Hair.1", plan.Closure.Vars);
            Assert.Equal("A.Hair.1", plan.Closure.Substitutions["A.Hair.2"]);
        }

        [Fact]
        public void Excluding_ignores_missing_references_of_dropped_vars()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "B.Other.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "Gone.Pack.1" } });

            Assert.Empty(plan.Closure.Missing);
        }

        [Fact]
        public void Excluding_one_version_keeps_the_others()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "A.Scene.2" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "A.Scene.latest" } });

            Assert.Equal(new[] { "A.Scene.2" }, plan.Closure.Vars);
            Assert.Empty(plan.AlsoLeftOut);
        }

        [Fact]
        public void Changing_only_exclusions_keeps_the_prior_version()
        {
            string path = Path.Combine(root, "trim.profile");
            LeanProfile first = new LeanProfile();
            first.Exclude.Add("A.One.1");
            LeanProfile second = new LeanProfile();
            second.Exclude.Add("A.Two.1");

            LeanProfiles.Save(first, path);
            LeanProfiles.Save(second, path);

            Assert.Equal(new[] { "A.One.1" }, LeanProfiles.Load(LeanProfiles.PreviousPath(path)).Exclude);
            Assert.Equal(new[] { "A.Two.1" }, LeanProfiles.Load(path).Exclude);
        }

        [Fact]
        public void ActiveLinks_counts_only_what_VaM_would_load()
        {
            string nested = Path.Combine(links, "Moved");
            Directory.CreateDirectory(nested);
            Link(Path.Combine(links, "A.Top.1.var"), Var("A.Top.1"));
            Link(Path.Combine(nested, "A.Nested.1.var"), Var("A.Nested.1"));
            Link(Path.Combine(links, "A.Broken.1.var"), Target("A.Broken.1"));
            Link(Path.Combine(links, "A.Off.1.var"), Var("A.Off.1"));
            File.WriteAllText(Path.Combine(links, "A.Off.1.var.disabled"), "");
            File.WriteAllText(Path.Combine(links, "A.Real.1.var"), "content");

            Assert.Equal(new[] { "A.Nested.1", "A.Top.1" },
                LeanProfiles.ActiveLinks(links, SearchOption.AllDirectories).OrderBy(name => name));
            Assert.Equal(new[] { "A.Top.1" }, LeanProfiles.ActiveLinks(links, SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public void Excluding_the_newest_version_keeps_an_older_one_that_names_its_package()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.2" }, new[] { "A.Scene.1", "A.Scene.2" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "A.Scene.latest" } });

            Assert.Equal(new[] { "A.Scene.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "A.Scene.2" }, plan.LeftOut);
        }

        [Fact]
        public void Excluding_moves_latest_references_to_a_version_that_stays()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.2" }, new[] { "A.Hair.1", "A.Hair.2", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.latest" } });

            Assert.Equal(new[] { "A.Hair.1", "B.Scene.1" }, plan.Closure.Vars);
        }

        [Fact]
        public void Excluding_keeps_a_version_named_exactly()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.2" }, new[] { "A.Hair.1", "A.Hair.2", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.2" } });

            Assert.Equal(new[] { "A.Hair.2 needed by B.Scene.1" }, plan.StillNeeded);
        }

        [Fact]
        public void Excluding_keeps_a_sibling_version_reached_through_another_package()
        {
            ProfilePlan plan = Excluding(new[] { "A.Scene.1" }, new[] { "A.Scene.1", "A.Scene.2", "B.Dep.1" },
                new Dictionary<string, string[]>
                {
                    ["A.Scene.1"] = new[] { "B.Dep.1" },
                    ["B.Dep.1"] = new[] { "A.Scene.latest" }
                });

            Assert.Equal(new[] { "A.Scene.2" }, plan.Closure.Vars);
        }

        [Fact]
        public void Excluding_keeps_the_only_version_that_meets_a_minimum()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.3" }, new[] { "A.Hair.1", "A.Hair.3", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.min2" } });

            Assert.Equal(new[] { "A.Hair.3 needed by B.Scene.1" }, plan.StillNeeded);
            Assert.Empty(plan.Closure.Substitutions);
        }

        [Fact]
        public void Excluding_moves_a_minimum_to_a_version_that_meets_it()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.3" }, new[] { "A.Hair.2", "A.Hair.3", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.min2" } });

            Assert.Equal(new[] { "A.Hair.3" }, plan.LeftOut);
        }

        [Fact]
        public void Excluding_keeps_a_newer_version_standing_in_for_a_missing_one()
        {
            ProfilePlan plan = Excluding(new[] { "A.Hair.3" }, new[] { "A.Hair.1", "A.Hair.3", "B.Scene.1" },
                new Dictionary<string, string[]> { ["B.Scene.1"] = new[] { "A.Hair.2" } });

            Assert.Equal(new[] { "A.Hair.3 needed by B.Scene.1" }, plan.StillNeeded);
        }

        [Fact]
        public void Excluding_keeps_other_versions_of_a_dependency_that_names_its_package()
        {
            ProfilePlan plan = Excluding(new[] { "X.Scene.1" }, new[] { "X.Scene.1", "B.Dep.1", "B.Dep.2" },
                new Dictionary<string, string[]>
                {
                    ["X.Scene.1"] = new[] { "B.Dep.1" },
                    ["B.Dep.1"] = new[] { "B.Dep.latest" }
                });

            Assert.Equal(new[] { "B.Dep.2" }, plan.Closure.Vars);
            Assert.Equal(new[] { "B.Dep.1" }, plan.AlsoLeftOut);
        }

        [Fact]
        public void Excluding_moves_a_base_latest_reference_to_a_version_that_stays()
        {
            ProfilePlan plan = Excluding(new[] { "A.Session.2" }, new[] { "A.Session.1", "A.Session.2" },
                new Dictionary<string, string[]>(), baseVars: new[] { "A.Session.latest" });

            Assert.Equal(new[] { "A.Session.1" }, plan.Closure.Vars);
            Assert.Equal(new[] { "A.Session.2" }, plan.LeftOut);
        }
    }
}
