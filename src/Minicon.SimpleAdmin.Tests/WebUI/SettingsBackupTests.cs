using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers;
using Minicon.SimpleAdmin.WebUI.Models.Backup;
using Minicon.SimpleAdmin.WebUI.Models.Scripts;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.Services.Backup;
using Minicon.SimpleAdmin.WebUI.Services.Control;
using Minicon.SimpleAdmin.WebUI.Services.Scripts;
using Moq;

namespace Minicon.SimpleAdmin.Tests.WebUI;

public sealed class SettingsBackupTests
{
    [Fact]
    public async Task Roundtrip_DownloadPreviewRestore_PreservesEverythingAndCreatesBackupAndAudit()
    {
        using var fixture = new Fixture();
        var originalConfig = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var originalScripts = await File.ReadAllBytesAsync(fixture.ScriptsPath);
        var download = await fixture.Backup.CreateDownloadAsync("DOMAIN\\Tester");
        download.FileName.Should().MatchRegex(@"^simpleadmin-einstellungen-.+-\d{8}-\d{6}\.zip$");

        fixture.WriteConfig(Fixture.ValidConfig("Geändert", "srv02"));
        fixture.WriteScripts(Fixture.ScriptsJson.Replace("\"one\"", "\"two\""));
        await File.WriteAllTextAsync(fixture.UploadPath("one.ps1"), "changed");
        await File.WriteAllTextAsync(fixture.UploadPath("extra.psm1"), "remove me");
        var before = fixture.Snapshot();

        var preview = await fixture.Preview(download.Content);
        fixture.AssertSnapshot(before);
        preview.Servers.Added.Should().Be(1);
        preview.Servers.Removed.Should().Be(1);
        preview.Manifest.Files.Select(f => f.Path).Should().Contain(["config.json", "scripts.json", "scripts/one.ps1"]);
        preview.Manifest.Files.Select(f => f.Path).Should().NotContain(name => name.Contains("history") || name.Contains("audit"));
        preview.ScriptFilesToRemove.Should().ContainSingle().Which.Should().Be("extra.psm1");
        await File.WriteAllTextAsync(fixture.UploadPath("later.ps1"), "keep me");

        var result = await fixture.Backup.RestoreAsync(preview.Token, "anonymous", removeUnlistedScripts: true);

        result.Success.Should().BeTrue(result.Message);
        result.PreviousBackupFileName.Should().NotBeNullOrWhiteSpace();
        (await File.ReadAllBytesAsync(fixture.ConfigPath)).Should().Equal(originalConfig);
        (await File.ReadAllBytesAsync(fixture.ScriptsPath)).Should().Equal(originalScripts);
        (await File.ReadAllTextAsync(fixture.UploadPath("one.ps1"))).Should().Be("param([string]$Name) # original");
        File.Exists(fixture.UploadPath("extra.psm1")).Should().BeFalse();
        File.Exists(fixture.UploadPath("later.ps1")).Should().BeTrue();
        fixture.Configuration.GetDomains().Should().ContainSingle(d => d.Name == "app-prod");
        fixture.Configuration.Config.ExtensionData.Should().ContainKey("futureRoot");
        fixture.Store.GetAll().Should().ContainSingle(d => d.Id == "one");

        var previous = Path.Combine(fixture.BackupDirectory, result.PreviousBackupFileName!);
        File.Exists(previous).Should().BeTrue();
        fixture.Backup.GetPage().AutomaticBackups.Select(b => b.FileName).Should().Contain(result.PreviousBackupFileName);
        Directory.EnumerateFiles(fixture.BackupDirectory, "config.json.*").Should().NotBeEmpty();
        Directory.EnumerateFiles(fixture.BackupDirectory, "scripts.json.*").Should().NotBeEmpty();
        var previousDownload = await fixture.Backup.CreateAutomaticDownloadAsync(result.PreviousBackupFileName!, "DOMAIN\\Tester");
        SettingsBackupArchive.Parse(previousDownload.Content).Manifest.BackupKind.Should().Be("full");

        var audit = fixture.ReadAudit();
        audit.Should().Contain(e => e.Action == "Download" && e.Outcome == "Succeeded" && e.User == "DOMAIN\\Tester");
        var restored = audit.Should().ContainSingle(e => e.Action == "Restore" && e.Outcome == "Succeeded").Subject;
        restored.FileName.Should().Be("backup.zip");
        restored.ManifestHash.Should().Be(preview.ManifestHash);
        fixture.Events.Verify(e => e.TryWrite(It.Is<SettingsBackupAuditEntry>(x => x.Action == "Restore" && x.Outcome == "Succeeded")), Times.Once);
    }

    [Fact]
    public async Task Roundtrip_ConfigWithCommentsAndTrailingCommas_UsesWorkerCompatibleReading()
    {
        using var fixture = new Fixture();
        const string config = """
            {
              // Vom ServiceConfigReader akzeptierte Schreibweise
              "servers": [{ "name": "srv01", "serviceTypes": [{ "type": "transfer" }], },],
              "domains": [{ "name": "app-prod", "environment": "production", "servers": ["srv01"], },],
            }
            """;

        var preview = await fixture.Preview(fixture.ValidZip(config: config));
        var result = await fixture.Backup.RestoreAsync(preview.Token, "anonymous");

        result.Success.Should().BeTrue(result.Message);
        (await File.ReadAllTextAsync(fixture.ConfigPath)).Should().Be(config);
        fixture.Configuration.GetDomains().Should().ContainSingle(d => d.Name == "app-prod");
        fixture.Configuration.HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Restore_ForeignUser_DoesNotConsumePreviewOrChangeFiles()
    {
        using var fixture = new Fixture();
        var preview = await fixture.Preview(fixture.ValidZip(), "DOMAIN\\Owner");
        var before = fixture.Snapshot();

        var foreign = await fixture.Backup.RestoreAsync(preview.Token, "DOMAIN\\Other");

        foreign.Success.Should().BeFalse();
        fixture.AssertSnapshot(before);
        fixture.PendingFiles().Should().ContainSingle();
        var owner = await fixture.Backup.RestoreAsync(preview.Token, "DOMAIN\\Owner");
        owner.Success.Should().BeTrue(owner.Message);
        fixture.PendingFiles().Should().BeEmpty();
        var replay = await fixture.Backup.RestoreAsync(preview.Token, "DOMAIN\\Owner");
        replay.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigurationService_NormalConfigPath_MatchesPreP6RoundtripAndSyntax()
    {
        using var fixture = new Fixture();
        var preP6Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var original = Fixture.ValidConfig("Vor P6");
        var expected = JsonSerializer.Serialize(JsonSerializer.Deserialize<Config>(original, preP6Options)!, preP6Options);
        fixture.WriteConfig(original);

        await fixture.Configuration.SaveToFileAsync();

        (await File.ReadAllTextAsync(fixture.ConfigPath)).Should().Be(expected);
        fixture.Configuration.HasChanges.Should().BeFalse();
        fixture.Configuration.LoadFromJson("""{"Servers":[{"Name":"srv01","ServiceTypes":[{"Type":"transfer"}]}]}""");
        fixture.Configuration.GetServers().Should().BeEmpty();
        Action comments = () => fixture.Configuration.LoadFromJson("{ // Kommentar\n}");
        comments.Should().Throw<JsonException>();
        Action trailingComma = () => fixture.Configuration.LoadFromJson("""{"servers":[],}""");
        trailingComma.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task FullRestore_WithoutExplicitDeletion_PreservesUnlistedScripts()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.UploadPath("extra.psm1"), "keep me");
        var preview = await fixture.Preview(fixture.ValidZip());

        preview.ScriptFilesToRemove.Should().ContainSingle().Which.Should().Be("extra.psm1");
        var result = await fixture.Backup.RestoreAsync(preview.Token, "anonymous");

        result.Success.Should().BeTrue(result.Message);
        (await File.ReadAllTextAsync(fixture.UploadPath("extra.psm1"))).Should().Be("keep me");
    }

    [Theory]
    [InlineData("zip-slip")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("bomb")]
    [InlineData("wrong-hash")]
    [InlineData("broken-config")]
    [InlineData("invalid-config")]
    public async Task MaliciousOrInvalidArchive_IsRejectedWithoutAnyChange(string kind)
    {
        using var fixture = new Fixture();
        var bytes = kind switch
        {
            "zip-slip" => Zip(("scripts/../../outside.ps1", "bad"), ("config.json", Fixture.ValidConfig()), ("scripts.json", Fixture.ScriptsJson)),
            "traversal" => Zip(("../outside.ps1", "bad"), ("config.json", Fixture.ValidConfig()), ("scripts.json", Fixture.ScriptsJson)),
            "absolute" => Zip(("/tmp/outside.ps1", "bad"), ("config.json", Fixture.ValidConfig()), ("scripts.json", Fixture.ScriptsJson)),
            "bomb" => Zip(("scripts/bomb.ps1", new string('0', 17 * 1024 * 1024)), ("config.json", Fixture.ValidConfig()), ("scripts.json", Fixture.ScriptsJson)),
            "wrong-hash" => ReplaceConfig(fixture.ValidZip(), Fixture.ValidConfig("Manipuliert")),
            "broken-config" => fixture.ValidZip(config: "not json"),
            "invalid-config" => fixture.ValidZip(config: "{\"servers\":[]}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var before = fixture.Snapshot();

        var act = async () => await fixture.Preview(bytes);

        await act.Should().ThrowAsync<SettingsBackupException>();
        fixture.AssertSnapshot(before);
        File.Exists(Path.Combine(fixture.Root, "outside.ps1")).Should().BeFalse();
        Directory.Exists(fixture.BackupDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task TooLargeUpload_AndEntryCountBomb_AreRejectedWithoutChange()
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var tooLarge = async () => await fixture.Backup.PreviewUploadAsync(Stream.Null,
            SettingsBackupArchive.MaxUploadBytes + 1, "bomb.zip", "anonymous");
        await tooLarge.Should().ThrowAsync<SettingsBackupException>().WithMessage("*50 MB*");

        var entries = Enumerable.Range(0, SettingsBackupArchive.MaxEntries + 1)
            .Select(i => (Name: $"scripts/f{i}.txt", Content: "x")).ToArray();
        var countBomb = async () => await fixture.Preview(Zip(entries));
        await countBomb.Should().ThrowAsync<SettingsBackupException>();
        fixture.AssertSnapshot(before);
        Directory.Exists(fixture.BackupDirectory).Should().BeFalse();
    }

    [Fact]
    public void Export_EnforcesPayloadLimitsBeforeCreatingArchive()
    {
        var payload = new SettingsBackupPayload("full",
            Encoding.UTF8.GetBytes(Fixture.ValidConfig()),
            Encoding.UTF8.GetBytes(Fixture.ScriptsJson),
            new Dictionary<string, byte[]> { ["huge.ps1"] = new byte[17 * 1024 * 1024] });
        var act = () => SettingsBackupArchive.Build(payload, "2.0.0", DateTimeOffset.UtcNow, "host", "user");
        act.Should().Throw<SettingsBackupException>().WithMessage("*Größenlimit*");
    }

    [Theory]
    [InlineData("CON.ps1")]
    [InlineData("nul.txt")]
    [InlineData("PRN.psm1")]
    [InlineData("AUX.psd1")]
    [InlineData("COM1.ps1")]
    [InlineData("LPT9.txt")]
    [InlineData("CLOCK$.ps1")]
    [InlineData("COM\u00b9.ps1")]
    [InlineData(" evil.ps1")]
    [InlineData("evil.ps1 ")]
    [InlineData(".hidden.ps1")]
    [InlineData("..ps1")]
    [InlineData("evil.ps1.")]
    [InlineData("evil\u202Egnp.ps1")]
    [InlineData("evil\u202Dgnp.ps1")]
    [InlineData("evil\u2066gnp.ps1")]
    [InlineData("evil\u200E.ps1")]
    [InlineData("evil\u061Cgnp.ps1")]
    [InlineData("evil\u007f.ps1")]
    public void UnsafeScriptFileNames_AreRejected(string fileName)
    {
        SettingsBackupArchive.IsAllowedScript(fileName).Should().BeFalse();
        var bytes = SettingsBackupArchive.Build(new SettingsBackupPayload("full",
                Encoding.UTF8.GetBytes(Fixture.ValidConfig()), Encoding.UTF8.GetBytes(Fixture.ScriptsJson),
                new Dictionary<string, byte[]> { [fileName] = Encoding.UTF8.GetBytes("bad") }),
            "2.0.0", DateTimeOffset.UtcNow, "host", "user");

        Action parse = () => SettingsBackupArchive.Parse(bytes);
        parse.Should().Throw<SettingsBackupException>();
    }

    [Fact]
    public async Task Preview_Expiry_RemovesZipAndRejectsToken()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = new Fixture(timeProvider: time);
        var preview = await fixture.Preview(fixture.ValidZip());
        var path = fixture.PendingFiles().Should().ContainSingle().Which;

        time.Advance(TimeSpan.FromMinutes(16));
        fixture.Backup.CleanupExpiredPreviews();

        File.Exists(path).Should().BeFalse();
        var result = await fixture.Backup.RestoreAsync(preview.Token, "anonymous");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("abgelaufen");
    }

    [Fact]
    public async Task Startup_RemovesOrphanedPreviewZips()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PreviewDirectory);
        var orphan = Path.Combine(fixture.PreviewDirectory, "orphan.zip");
        var unrelated = Path.Combine(fixture.PreviewDirectory, "keep.tmp");
        await File.WriteAllBytesAsync(orphan, new byte[] { 1, 2, 3 });
        await File.WriteAllTextAsync(unrelated, "keep");

        await fixture.Backup.StartAsync(default);
        try
        {
            File.Exists(orphan).Should().BeFalse();
            File.Exists(unrelated).Should().BeTrue();
        }
        finally
        {
            await fixture.Backup.StopAsync(default);
        }
    }

    [Fact]
    public async Task Restore_WithoutAuthentication_IsBlockedAndAuditedWithoutChange()
    {
        var host = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Enabled"] = "false"
        }).Build();
        var options = SettingsBackupOptions.Resolve(host, authenticationEnabled: false);
        options.RestoreEnabled.Should().BeFalse();
        options.DisabledReason.Should().Contain("AllowWithoutAuthentication");
        using var fixture = new Fixture(options: options);
        var before = fixture.Snapshot();

        var preview = async () => await fixture.Preview(fixture.ValidZip());
        await preview.Should().ThrowAsync<SettingsBackupException>();
        var serviceResult = await fixture.Backup.RestoreAsync("forged-token", "anonymous");
        serviceResult.Success.Should().BeFalse();
        var controller = new SettingsBackupController(fixture.Backup, options)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var result = (await controller.Restore("forged-token")).Should().BeOfType<ObjectResult>().Subject;

        result.StatusCode.Should().Be(403);
        fixture.AssertSnapshot(before);
        fixture.ReadAudit().Should().ContainSingle(e => e.Action == "Restore" && e.Outcome == "Rejected" && e.User == "anonymous");
        Directory.Exists(fixture.BackupDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preview_ChecksEncryptedValuesAndWarnsForNewerVersion(bool sameKey)
    {
        const string keyA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        const string keyB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
        using var fixture = new Fixture(encryptionKey: keyA, currentKey: sameKey ? keyA : keyB);
        var bytes = fixture.ValidZip(version: "999.0.0");

        var preview = await fixture.Preview(bytes);

        preview.NewerVersion.Should().BeTrue();
        preview.EncryptedValues.Should().Be(1);
        preview.UndecryptableValues.Should().Be(sameKey ? 0 : 1);
        preview.ManifestHash.Should().HaveLength(64);
    }

    [Fact]
    public async Task AutomaticF4ConfigBackup_HasManifestAndRestoresOnlyConfig()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.BackupDirectory);
        var originalConfig = await File.ReadAllBytesAsync(fixture.ConfigPath);
        var backupName = "config.json." + JsonFileBackup.UtcTimestamp();
        await File.WriteAllBytesAsync(Path.Combine(fixture.BackupDirectory, backupName), originalConfig);
        fixture.WriteConfig(Fixture.ValidConfig("Geändert"));
        fixture.WriteScripts(Fixture.ScriptsJson.Replace("Eins", "Aktuell"));
        var changedScripts = await File.ReadAllBytesAsync(fixture.ScriptsPath);

        fixture.Backup.GetPage().AutomaticBackups.Select(b => b.FileName).Should().Contain(backupName);
        var download = await fixture.Backup.CreateAutomaticDownloadAsync(backupName, "anonymous");
        var package = SettingsBackupArchive.Parse(download.Content);
        package.Manifest.BackupKind.Should().Be("config");
        package.Scripts.Should().BeNull();
        var preview = await fixture.Backup.PreviewAutomaticAsync(backupName, "anonymous");
        var result = await fixture.Backup.RestoreAsync(preview.Token, "anonymous");

        result.Success.Should().BeTrue(result.Message);
        (await File.ReadAllBytesAsync(fixture.ConfigPath)).Should().Equal(originalConfig);
        (await File.ReadAllBytesAsync(fixture.ScriptsPath)).Should().Equal(changedScripts);
    }

    [Theory]
    [InlineData("Succeeded", 3200)]
    [InlineData("Rejected", 3201)]
    [InlineData("Failed", 3202)]
    public async Task WindowsEventAudit_WritesBackupResultThroughSharedChannel(string outcome, int eventId)
    {
        var sink = new Mock<IWindowsEventSink>();
        sink.SetupGet(s => s.IsWindows).Returns(true);
        sink.Setup(s => s.IsApplicationSource(It.IsAny<string>())).Returns(true);
        var written = new TaskCompletionSource<(int Id, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Setup(s => s.Write(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Callback<string, int, string>((_, id, message) => written.TrySetResult((id, message)));
        using var worker = new WindowsActionEventAudit(sink.Object, NullLogger<WindowsActionEventAudit>.Instance);
        await worker.StartAsync(default);
        var entry = new SettingsBackupAuditEntry(DateTimeOffset.UtcNow, "DOMAIN\\Tester", "Restore",
            "backup.zip", new string('a', 64), outcome);

        worker.TryWrite(entry);
        var result = await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);

        result.Id.Should().Be(eventId);
        using var json = JsonDocument.Parse(result.Message);
        json.RootElement.GetProperty("User").GetString().Should().Be("DOMAIN\\Tester");
        json.RootElement.GetProperty("FileName").GetString().Should().Be("backup.zip");
        json.RootElement.GetProperty("ManifestHash").GetString().Should().Be(new string('a', 64));
    }

    [Fact]
    public void BackupActions_RequirePostAndAntiforgery()
    {
        foreach (var name in new[] { "Download", "AutomaticDownload", "Preview", "AutomaticPreview", "Restore" })
        {
            var method = typeof(SettingsBackupController).GetMethod(name)!;
            method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Should().NotBeEmpty();
            method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).Should().ContainSingle();
            method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Should().BeEmpty();
        }
        typeof(SettingsBackupController).GetMethod("Index")!.GetCustomAttributes(typeof(HttpPostAttribute), true).Should().BeEmpty();
    }

    private static byte[] Zip(params (string Name, string Content)[] entries) =>
        Zip(entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                var item = archive.CreateEntry(entry.Name);
                using var stream = item.Open();
                stream.Write(entry.Content);
            }
        return output.ToArray();
    }

    private static byte[] ReplaceConfig(byte[] zip, string config)
    {
        using var output = new MemoryStream();
        output.Write(zip);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Update, true);
        archive.GetEntry("config.json")!.Delete();
        var entry = archive.CreateEntry("config.json");
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(config);
        stream.Write(bytes);
        archive.Dispose();
        return output.ToArray();
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan delta) => now = now.Add(delta);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string ScriptsJson = """
            {
              "scripts": [{
                "id": "one", "name": "Eins", "path": "/tmp/one.ps1", "parameters": [],
                "futureScript": { "keep": true }
              }],
              "futureScriptsRoot": 7
            }
            """;

        internal readonly string Root = Path.Combine(Path.GetTempPath(), "sa-p6-" + Guid.NewGuid().ToString("N"));
        internal readonly ConfigurationService Configuration;
        internal readonly ScriptDefinitionStore Store;
        internal readonly SettingsBackupService Backup;
        internal readonly Mock<ISettingsBackupEventAudit> Events = new();
        internal string ConfigPath => Path.Combine(Root, "config.json");
        internal string ScriptsPath => Path.Combine(Root, "scripts.json");
        internal string BackupDirectory => Path.Combine(Root, "backup");
        internal string PreviewDirectory => Path.Combine(Root, "pending");

        internal Fixture(SettingsBackupOptions? options = null, string? encryptionKey = null, string? currentKey = null,
            TimeProvider? timeProvider = null)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(UploadPath(""));
            var host = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:ConnectionStringKey"] = currentKey ?? encryptionKey ?? ""
            }).Build();
            Configuration = new ConfigurationService(host, NullLogger<ConfigurationService>.Instance);
            var encrypted = encryptionKey == null ? null : ConnectionStringEncryption.Encrypt("Server=db;Password=secret", encryptionKey);
            File.WriteAllText(ConfigPath, ValidConfig(encrypted: encrypted));
            Configuration.LoadFromFile(File.ReadAllText(ConfigPath), ConfigPath);
            File.WriteAllText(ScriptsPath, ScriptsJson);
            Store = new ScriptDefinitionStore(ScriptsPath, NullLogger<ScriptDefinitionStore>.Instance);
            File.WriteAllText(UploadPath("one.ps1"), "param([string]$Name) # original");
            var paths = new ScriptPathPolicy(UploadPath(""), [], []);
            var status = new StatusReaderService(Path.Combine(Root, "status"), new Mock<IAcknowledgeService>().Object,
                Configuration, NullLogger<StatusReaderService>.Instance, stateDirectory: Root);
            var audit = new SettingsBackupAuditLog(Root, NullLogger<SettingsBackupAuditLog>.Instance);
            Backup = new SettingsBackupService(Configuration, Store, paths, status, options ?? new SettingsBackupOptions(),
                audit, Events.Object, NullLogger<SettingsBackupService>.Instance, timeProvider ?? TimeProvider.System,
                Root, PreviewDirectory);
        }

        internal static string ValidConfig(string title = "Original", string server = "srv01", string? encrypted = null)
        {
            var checks = encrypted == null ? "{}" : "{\"sqlQueries\":{\"enabled\":true,\"checks\":[{\"name\":\"db\",\"connectionString\":\"" + encrypted + "\",\"queries\":[]}]}}";
            return $$"""
                {
                  "general": { "title": {{JsonSerializer.Serialize(title)}}, "futureGeneral": true },
                  "servers": [{
                    "name": {{JsonSerializer.Serialize(server)}}, "active": true,
                    "serviceTypes": [{ "type": "transfer", "futureType": 3 }],
                    "checks": {{checks}},
                    "futureServer": { "keep": [1, 2] }
                  }],
                  "environments": {
                    "production": { "services": [{{JsonSerializer.Serialize(server)}}], "futureEnvironment": "yes" }
                  },
                  "domains": [{
                    "name": "app-prod", "environment": "production", "servers": [{{JsonSerializer.Serialize(server)}}],
                    "futureDomain": { "tls": true }
                  }],
                  "futureRoot": { "keep": "yes" }
                }
                """;
        }

        internal string UploadPath(string name) => Path.Combine(Root, "scripts", name);

        internal byte[] ValidZip(string? config = null, string? scripts = null, string version = "2.0.0") =>
            SettingsBackupArchive.Build(new SettingsBackupPayload("full",
                Encoding.UTF8.GetBytes(config ?? File.ReadAllText(ConfigPath)),
                Encoding.UTF8.GetBytes(scripts ?? File.ReadAllText(ScriptsPath)),
                new Dictionary<string, byte[]> { ["one.ps1"] = Encoding.UTF8.GetBytes("param([string]$Name) # original") }),
                version, DateTimeOffset.UtcNow, "test-host", "backup-user");

        internal async Task<SettingsBackupPreview> Preview(byte[] bytes, string user = "anonymous")
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return await Backup.PreviewUploadAsync(stream, bytes.LongLength, "backup.zip", user);
        }

        internal string[] PendingFiles() => Directory.Exists(PreviewDirectory)
            ? Directory.EnumerateFiles(PreviewDirectory, "*.zip", SearchOption.TopDirectoryOnly).ToArray()
            : [];

        internal void WriteConfig(string json)
        {
            File.WriteAllText(ConfigPath, json);
            Configuration.LoadFromFile(json, ConfigPath);
        }

        internal void WriteScripts(string json)
        {
            File.WriteAllText(ScriptsPath, json);
            Store.Reload();
        }

        internal Dictionary<string, byte[]> Snapshot()
        {
            var files = new[] { ConfigPath, ScriptsPath }.Concat(Directory.Exists(UploadPath(""))
                ? Directory.EnumerateFiles(UploadPath(""), "*", SearchOption.TopDirectoryOnly) : []);
            return files.ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllBytes);
        }

        internal void AssertSnapshot(Dictionary<string, byte[]> expected)
        {
            var actual = Snapshot();
            actual.Keys.Should().BeEquivalentTo(expected.Keys);
            foreach (var key in expected.Keys) actual[key].Should().Equal(expected[key], key);
        }

        internal List<SettingsBackupAuditEntry> ReadAudit()
        {
            var path = Path.Combine(Root, "settings-backup-actions.json");
            return JsonSerializer.Deserialize<List<SettingsBackupAuditEntry>>(File.ReadAllText(path),
                AtomicAuditLog<SettingsBackupAuditEntry>.JsonOptions)!;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
