using Gyrodial.Core;

namespace Gyrodial.Integrations.Tests;

/// <summary>0.3.0 renamed the app from Puck to Gyrodial: its files move to the new folder once.</summary>
public class MigrationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gyrodial-migrate-" + Guid.NewGuid().ToString("N"));
    string Old => Path.Combine(_dir, "Puck");
    string New => Path.Combine(_dir, "Gyrodial");

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Moves_the_old_folder_once()
    {
        Directory.CreateDirectory(Path.Combine(Old, "logs"));
        File.WriteAllText(Path.Combine(Old, "settings.json"), "{\"HostId\":\"abc\"}");
        File.WriteAllText(Path.Combine(Old, "hook.key"), "k");
        File.WriteAllText(Path.Combine(Old, "logs", "puck-20261110.log"), "line");

        Assert.NotNull(AppPaths.MigrateDataDir(Old, New));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("{\"HostId\":\"abc\"}", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(New, "hook.key")));
        Assert.True(File.Exists(Path.Combine(New, "logs", "puck-20261110.log")));

        Assert.Null(AppPaths.MigrateDataDir(Old, New));  // nothing left to move
    }

    [Fact]
    public void Never_overwrites_the_new_folder()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "settings.json"), "old");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "settings.json"), "new");

        Assert.Null(AppPaths.MigrateDataDir(Old, New));
        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.True(Directory.Exists(Old));
    }

    [Fact]
    public void Copies_when_a_file_is_still_open()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "settings.json"), "s");
        using (new FileStream(Path.Combine(Old, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.StartsWith("Copied", AppPaths.MigrateDataDir(Old, New));
        }
        Assert.Equal("s", File.ReadAllText(Path.Combine(New, "settings.json")));
    }

    [Fact]
    public void Loads_settings_saved_by_the_old_app()
    {
        var old = """
            {
              "HostId": "0123456789abcdef",
              "Pucks": [ { "Id": "a1b2c3d4e5f60708", "Name": "Puck-1D4D", "BleAddress": 44530723413325 } ],
              "Brightness": 55,
              "ClaudeSystemPrompt": "You are Claude, answering through Puck: a small round screen on the user's desk. They speak to you and read your reply on a 1.75-inch round display, so keep answers short: one to three plain sentences, no markdown, no lists, no code blocks unless they ask for code."
            }
            """;
        var s = SettingsStore.Parse(old);
        Assert.Equal("0123456789abcdef", s.HostId);
        var dial = Assert.Single(s.Dials);
        Assert.Equal("a1b2c3d4e5f60708", dial.Id);
        Assert.Equal(44530723413325UL, dial.BleAddress);
        Assert.Equal(55, s.Brightness);
        Assert.Equal(new GyrodialSettings().ClaudeSystemPrompt, s.ClaudeSystemPrompt);  // the old default is replaced
        Assert.DoesNotContain("Puck", s.ClaudeSystemPrompt);

        // A prompt the user wrote stays as it is
        Assert.Equal("Answer like Puck would.", SettingsStore.Parse("""{ "ClaudeSystemPrompt": "Answer like Puck would." }""").ClaudeSystemPrompt);
    }

    [Theory]
    [InlineData("Gyrodial-1D4D", true)]
    [InlineData("Puck-1D4D", true)]      // firmware before 0.3.0
    [InlineData("Pixel 9", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_dial_names(string? name, bool dial) => Assert.Equal(dial, Protocol.IsDialName(name));
}
