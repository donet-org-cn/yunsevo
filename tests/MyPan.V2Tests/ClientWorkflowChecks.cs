using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MyPan.Contracts;
using MyPan.Sync;

internal static class ClientWorkflowChecks
{
    public static void Run(string root, string server, Action<bool, string> assert)
    {
        var user = new UserInfo(Guid.NewGuid(), "test-user", "Test", false, false);
        var profile = new SyncTaskProfile(Guid.NewGuid(), server, user.Username, user.Id, "Private", "个人/test-user", Path.Combine(root, "profile-a"), false, true, Guid.NewGuid());
        var other = profile with { Id = Guid.NewGuid(), Name = "Public", Remote = "公共", Local = Path.Combine(root, "profile-b"), Enabled = false };
        SyncProfiles.ValidateNew(other, [profile]);
        assert(true, "Independent local and remote roots support multiple tasks");
        void Reject(SyncTaskProfile candidate, string message)
        {
            var rejected = false; try { SyncProfiles.ValidateNew(candidate, [profile]); } catch (ArgumentException) { rejected = true; }
            assert(rejected, message);
        }
        Reject(other with { Local = profile.Local }, "Duplicate local roots cannot be assigned to another task");
        Reject(other with { Local = Path.Combine(profile.Local, "child"), UserId = Guid.NewGuid() }, "Nested local roots are rejected even across accounts");
        Reject(other with { Remote = profile.Remote + "/child" }, "Overlapping cloud roots cannot run conflicting tasks");
        assert(!SyncProfiles.Matches(profile, server, user with { Id = Guid.NewGuid() }), "A different login cannot acquire another user's saved task");
        var file = Path.Combine(root, "profiles.json");
        SyncProfiles.Save(file, new(server, user.Username, [profile, other]));
        var loaded = SyncProfiles.Load(file);
        assert(loaded.Tasks.SequenceEqual(new[] { profile, other }), "Multiple task modes, identities and pause settings round-trip without credentials");
        var mode = OperatingSystem.IsWindows() ? "placeholders" : "full-copy";
        var oldBinding = server.TrimEnd('/') + "\n" + user.Id + "\n" + profile.Remote + "\n" + profile.Local + "\n" + mode;
        assert(SyncProfiles.StatePath(root, profile with { OnDemand = true }) == Path.Combine(root, "states", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldBinding))) + ".json"), "Task migration preserves the previous desktop sync-state filename");
        var legacy = new JsonObject { ["Server"] = server, ["Username"] = user.Username, ["Remote"] = profile.Remote, ["Local"] = profile.Local, ["OnDemand"] = true };
        File.WriteAllText(file, legacy.ToJsonString()); var migrated = SyncProfiles.Load(file);
        assert(migrated.Tasks.Count == 1 && migrated.Tasks[0].UserId == Guid.Empty && migrated.Tasks[0].Remote == profile.Remote && migrated.Tasks[0].Local == profile.Local, "Legacy single-task preferences wait for the matching account before binding");
        File.WriteAllText(file, "{\"Server\":\"http://localhost\",\"Username\":\"test\",\"Tasks\":null}");
        var broken = File.ReadAllText(file); var invalid = false;
        try { SyncProfiles.Load(file); } catch (InvalidDataException) { invalid = true; }
        assert(invalid && File.ReadAllText(file) == broken, "Damaged task lists cannot silently discard saved configuration");
        var directory = Path.Combine(root, "deletion-safety"); Directory.CreateDirectory(directory);
        using var safety = new DeletionSafety(directory);
        safety.ConnectionChanged(true, false);
        var startup = safety.Request("startup.txt", false, true);
        safety.CompletePass();
        assert(startup.DetectedAtStartup && !startup.DetectedWhileOnline && !safety.Request("startup.txt", false, false).DetectedWhileOnline, "Deferred startup deletion cannot become an automatic online deletion on a later pass");
        assert(safety.Request("online.txt", false, false).DetectedWhileOnline, "An established online task can delete without a confirmation callback");
        safety.ConnectionChanged(false, false);
        var offline = safety.Request("folder/offline.txt", false, false);
        safety.ConnectionChanged(true, false);
        assert(!offline.DetectedWhileOnline && !safety.Request("folder/offline.txt", false, false).DetectedWhileOnline, "Reconnection preserves an offline deletion's confirmation requirement");
        assert(!safety.Request("folder", true, false).DetectedWhileOnline, "Deleting a parent cannot bypass a child's pending offline confirmation");
        safety.Present(["folder/offline.txt"]);
        assert(safety.Request("folder/offline.txt", false, false).DetectedWhileOnline, "Recreating a file clears its earlier offline deletion context");
    }
}
