using System.Text.Json.Nodes;
using Puck.Integrations.ClaudeCode;

namespace Puck.Integrations.Tests;

public class HookInstallerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "puck-hooks-" + Guid.NewGuid().ToString("N"));
    string SettingsPath => Path.Combine(_dir, ".claude", "settings.json");

    public HookInstallerTests() => Directory.CreateDirectory(Path.Combine(_dir, ".claude"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    const string Existing = """
        {
          "model": "opus",
          "permissions": { "allow": ["Bash(git status)"] },
          "hooks": {
            "PermissionRequest": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "other-tool.exe" } ] }
            ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ]
          },
        }
        """;

    [Fact]
    public void Installs_alongside_existing_settings_and_is_idempotent()
    {
        File.WriteAllText(SettingsPath, Existing);
        var exe = @"C:\Program Files\Puck\puck-hook.exe";
        var installer = new HookInstaller(SettingsPath, exe);
        Assert.Equal(HookInstaller.HookState.NotInstalled, installer.GetState());

        var backup = installer.Install(120);
        Assert.True(File.Exists(backup));
        installer.Install(120);  // again: still one entry
        Assert.Equal(HookInstaller.HookState.Installed, installer.GetState());

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", root["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.NotNull(root["hooks"]!["Stop"]);

        var groups = root["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(2, groups.Count);  // theirs + ours
        Assert.Equal("other-tool.exe", groups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var ours = groups[1]!;
        Assert.Equal("*", ours["matcher"]!.GetValue<string>());
        var handler = ours["hooks"]![0]!;
        Assert.Equal("command", handler["type"]!.GetValue<string>());
        Assert.Equal(exe, handler["command"]!.GetValue<string>());
        Assert.Equal("permission", handler["args"]![0]!.GetValue<string>());
        Assert.Equal(120, handler["timeout"]!.GetValue<int>());

        var notify = root["hooks"]!["Notification"]!.AsArray();
        Assert.Single(notify);
        Assert.Equal("notify", notify[0]!["hooks"]![0]!["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Replaces_an_old_location_and_removes_cleanly()
    {
        new HookInstaller(SettingsPath, @"C:\Old\puck-hook.exe").Install(60);
        var installer = new HookInstaller(SettingsPath, @"C:\New\puck-hook.exe");
        Assert.Equal(HookInstaller.HookState.Stale, installer.GetState());

        installer.Install(60);
        Assert.Equal(HookInstaller.HookState.Installed, installer.GetState());
        var commands = JsonNode.Parse(File.ReadAllText(SettingsPath))!["hooks"]!["PermissionRequest"]!.AsArray()
            .SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>()).ToList();
        Assert.Equal([@"C:\New\puck-hook.exe"], commands);

        Assert.NotNull(installer.Remove());
        Assert.Equal(HookInstaller.HookState.NotInstalled, installer.GetState());
        Assert.Null(JsonNode.Parse(File.ReadAllText(SettingsPath))!["hooks"]);
        Assert.Null(installer.Remove());  // nothing left to remove
    }

    [Fact]
    public void Says_when_the_timeout_changed()
    {
        var installer = new HookInstaller(SettingsPath, @"C:\Puck\puck-hook.exe");
        installer.Install(120);
        Assert.Equal(HookInstaller.HookState.Installed, installer.GetState(120));
        Assert.Equal(HookInstaller.HookState.OldTimeout, installer.GetState(60));
    }

    [Theory]
    [InlineData("{\n  // my notes\n  \"model\": \"opus\"\n}", "comments")]
    [InlineData("{ \"model\": \"opus\", \"model\": \"sonnet\" }", "written twice")]
    [InlineData("{ \"permissions\": { \"allow\": [], \"allow\": [] } }", "written twice")]
    [InlineData("{ \"hooks\": [] }", "isn't an object")]
    [InlineData("{ \"hooks\": { \"PermissionRequest\": {} } }", "isn't a list")]
    [InlineData("[1, 2]", "isn't a JSON object")]
    [InlineData("{ not json", "isn't valid JSON")]
    public void Leaves_files_it_cant_rewrite_alone(string content, string why)
    {
        File.WriteAllText(SettingsPath, content);
        var installer = new HookInstaller(SettingsPath, @"C:\Puck\puck-hook.exe");
        var e = Assert.Throws<HookSettingsException>(() => installer.Install(60));
        Assert.Contains(why, e.Message);
        Assert.Equal(content, File.ReadAllText(SettingsPath));  // untouched
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.puck-backup-*"));
        Assert.Equal(HookInstaller.HookState.NotInstalled, installer.GetState());  // and reading doesn't throw
    }

    [Fact]
    public void Backups_never_overwrite_each_other()
    {
        File.WriteAllText(SettingsPath, Existing);
        var installer = new HookInstaller(SettingsPath, @"C:\Puck\puck-hook.exe");
        var first = installer.Install(60);
        var second = installer.Install(90);  // same second, usually
        Assert.NotEqual(first, second);
        Assert.Equal(Existing, File.ReadAllText(first!));  // the original survives
    }

    [Fact]
    public void Remove_keeps_other_hooks()
    {
        File.WriteAllText(SettingsPath, Existing);
        var installer = new HookInstaller(SettingsPath, @"C:\Puck\puck-hook.exe");
        installer.Install(60);
        installer.Remove();
        var hooks = JsonNode.Parse(File.ReadAllText(SettingsPath))!["hooks"]!;
        Assert.Single(hooks["PermissionRequest"]!.AsArray());
        Assert.Null(hooks["Notification"]);
        Assert.NotNull(hooks["Stop"]);
    }
}
