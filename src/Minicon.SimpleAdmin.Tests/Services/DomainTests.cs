using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Minicon.SimpleAdmin.HtmlGenerator;
using Minicon.SimpleAdmin.Models.Config;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.WebUI.Controllers;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.WebUI.ViewModels;

namespace Minicon.SimpleAdmin.Tests.Services;

public class DomainNamingConventionTests
{
    private static readonly string[] StandardEnvs = { "production", "ref", "development" };

    [Theory]
    [InlineData("app-services-dev", "development")]
    [InlineData("app-services-ref", "ref")]
    [InlineData("app-services-prod", "production")]
    [InlineData("APP-SERVICES-DEV", "development")]
    [InlineData("app-services-dev.example.org", "development")]
    public void SuggestEnvironment_KnownSuffix_ReturnsMappedEnvironment(string name, string expected)
    {
        DomainNamingConvention.SuggestEnvironment(name, StandardEnvs).Should().Be(expected);
    }

    [Theory]
    [InlineData("app-services")]
    [InlineData("app-services.example.org")]
    [InlineData("portal")]
    public void SuggestEnvironment_NoSuffix_ReturnsProduction(string name)
    {
        DomainNamingConvention.SuggestEnvironment(name, StandardEnvs).Should().Be("production");
    }

    [Fact]
    public void SuggestEnvironment_NoSuffix_WithoutProductionKey_ReturnsNull()
    {
        DomainNamingConvention.SuggestEnvironment("app-services", new[] { "ref", "development" }).Should().BeNull();
    }

    [Fact]
    public void SuggestEnvironment_UnknownSuffix_IsTreatedAsNoSuffix()
    {
        // "-test" is not a known suffix → name without known suffix → possible PROD domain
        DomainNamingConvention.SuggestEnvironment("app-services-test", StandardEnvs).Should().Be("production");
    }

    [Fact]
    public void SuggestEnvironment_SuffixMappedToMissingEnvironment_ReturnsNull()
    {
        DomainNamingConvention.SuggestEnvironment("app-services-dev", new[] { "production" }).Should().BeNull();
    }

    [Fact]
    public void SuggestEnvironment_SuffixOnly_IsNotASuffix()
    {
        // "dev" alone (no base name) does not carry the "-dev" suffix
        DomainNamingConvention.SuggestEnvironment("dev", StandardEnvs).Should().Be("production");
    }

    [Fact]
    public void SuggestEnvironment_ReturnsConfiguredKeyCasing()
    {
        DomainNamingConvention.SuggestEnvironment("app-ref", new[] { "Production", "REF" }).Should().Be("REF");
    }

    [Fact]
    public void SuggestEnvironment_EmptyName_ReturnsNull()
    {
        DomainNamingConvention.SuggestEnvironment("  ", StandardEnvs).Should().BeNull();
    }

    [Fact]
    public void OverrideMap_AddsNewSuffix()
    {
        var envs = new[] { "production", "ref", "development", "test" };
        var map = new Dictionary<string, string> { ["-test"] = "test" };

        DomainNamingConvention.SuggestEnvironment("app-test", envs, map).Should().Be("test");
        DomainNamingConvention.SuggestEnvironment("app-dev", envs, map).Should().Be("development");
    }

    [Fact]
    public void OverrideMap_WithoutLeadingDash_IsNormalised()
    {
        var map = new Dictionary<string, string> { ["qs"] = "ref" };
        DomainNamingConvention.SuggestEnvironment("app-qs", StandardEnvs, map).Should().Be("ref");
    }

    [Fact]
    public void OverrideMap_OverridesBuiltInSuffix()
    {
        var envs = new[] { "production", "entwicklung" };
        var map = new Dictionary<string, string> { ["-dev"] = "entwicklung" };
        DomainNamingConvention.SuggestEnvironment("app-dev", envs, map).Should().Be("entwicklung");
    }

    [Fact]
    public void OverrideMap_EmptyValue_RemovesBuiltInSuffix()
    {
        var map = new Dictionary<string, string> { ["-ref"] = "" };
        DomainNamingConvention.GetEffectiveSuffixMap(map).Should().NotContainKey("-ref");
        DomainNamingConvention.SuggestEnvironment("app-ref", StandardEnvs, map).Should().Be("production");
    }

    [Fact]
    public void OverrideMap_LongestSuffixWins()
    {
        var envs = new[] { "production", "preprod" };
        var map = new Dictionary<string, string> { ["-preprod"] = "preprod" };
        DomainNamingConvention.SuggestEnvironment("app-preprod", envs, map).Should().Be("preprod");
        DomainNamingConvention.SuggestEnvironment("app-prod", envs, map).Should().Be("production");
    }

    [Fact]
    public void BothProductionVariants_AreConsistentWithProduction()
    {
        var plain = new Domain { Name = "app-services", Environment = "production" };
        var prod = new Domain { Name = "app-services-prod", Environment = "production" };

        DomainNamingConvention.IsConsistent(plain, StandardEnvs).Should().BeTrue();
        DomainNamingConvention.IsConsistent(prod, StandardEnvs).Should().BeTrue();
    }

    [Fact]
    public void Mismatch_WithoutException_ShowsWarning()
    {
        var domain = new Domain { Name = "app-services-dev", Environment = "production" };

        var result = DomainNamingConvention.Evaluate(domain, StandardEnvs);

        result.IsConsistent.Should().BeFalse();
        result.ShowWarning.Should().BeTrue();
        result.SuggestedEnvironment.Should().Be("development");
        result.MatchedSuffix.Should().Be("-dev");
    }

    [Fact]
    public void Mismatch_WithException_SuppressesWarningButStaysInconsistent()
    {
        var domain = new Domain { Name = "legacy-portal", Environment = "ref", AllowNameMismatch = true };

        var result = DomainNamingConvention.Evaluate(domain, StandardEnvs);

        result.IsConsistent.Should().BeFalse();
        result.ShowWarning.Should().BeFalse();
    }

    [Fact]
    public void NoSuggestionPossible_IsConsistent()
    {
        var domain = new Domain { Name = "app-services", Environment = "ref" };
        DomainNamingConvention.IsConsistent(domain, new[] { "ref" }).Should().BeTrue();
    }

    [Fact]
    public void ConfigOverload_UsesConfigSuffixMap()
    {
        var config = new Config
        {
            Environments = { ["production"] = new(), ["test"] = new() },
            DomainSuffixMap = new Dictionary<string, string> { ["-test"] = "test" }
        };

        DomainNamingConvention.SuggestEnvironment("app-test", config).Should().Be("test");
        DomainNamingConvention.IsConsistent(new Domain { Name = "app-test", Environment = "test" }, config).Should().BeTrue();
    }
}

public class DomainConfigTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Config SampleConfig() => new()
    {
        Environments =
        {
            ["production"] = new EnvironmentConfig { Services = { "srv-prod-01", "srv-prod-02" } },
            ["ref"] = new EnvironmentConfig { Transfer = { "srv-ref-01" } },
            ["development"] = new EnvironmentConfig { Biztalk = { "srv-dev-01" } }
        },
        Servers =
        {
            new Server { Name = "srv-prod-01", Active = true },
            new Server { Name = "srv-prod-02", Active = true },
            new Server { Name = "srv-ref-01", Active = true },
            new Server { Name = "srv-dev-01", Active = true },
            new Server { Name = "srv-orphan", Active = true }
        }
    };

    [Fact]
    public void Deserialize_WithoutDomainsBlock_YieldsEmptyList()
    {
        var config = JsonSerializer.Deserialize<Config>("""{ "servers": [] }""", JsonOptions)!;

        config.Domains.Should().NotBeNull().And.BeEmpty();
        config.DomainSuffixMap.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithDomains_ReadsAllFields()
    {
        const string json = """
        {
          "environments": { "production": { "services": [ "srv-prod-01" ] } },
          "domains": [
            { "name": "app-services", "environment": "production", "servers": [ "srv-prod-01" ],
              "netScalerVServer": "vs_app", "healthCheck": "hc", "allowNameMismatch": false },
            { "name": "app-services-prod", "environment": "production", "servers": [ "srv-prod-01" ],
              "netScalerVServer": "vs_app" },
            { "name": "legacy", "environment": "production", "allowNameMismatch": true }
          ],
          "domainSuffixMap": { "-test": "test" }
        }
        """;

        var config = JsonSerializer.Deserialize<Config>(json, JsonOptions)!;

        config.Domains.Should().HaveCount(3);
        config.Domains[0].Name.Should().Be("app-services");
        config.Domains[0].NetScalerVServer.Should().Be("vs_app");
        config.Domains[0].HealthCheck.Should().Be("hc");
        config.Domains[1].NetScalerVServer.Should().Be("vs_app", "several domains may share one vServer");
        config.Domains[2].AllowNameMismatch.Should().BeTrue();
        config.Domains[2].Servers.Should().BeEmpty();
        config.DomainSuffixMap.Should().ContainKey("-test").WhoseValue.Should().Be("test");
    }

    [Fact]
    public void Serialize_RoundTrip_KeepsDomains()
    {
        var config = SampleConfig();
        config.Domains.Add(new Domain { Name = "app-dev", Environment = "development", Servers = { "srv-dev-01" } });

        var json = JsonSerializer.Serialize(config, JsonOptions);
        json.Should().Contain("\"domains\"").And.NotContain("domainSuffixMap");

        var back = JsonSerializer.Deserialize<Config>(json, JsonOptions)!;
        back.Domains.Should().ContainSingle(d => d.Name == "app-dev" && d.Servers.Contains("srv-dev-01"));
    }

    [Fact]
    public void GetServersInEnvironment_FiltersByEnvironment()
    {
        var config = SampleConfig();

        config.GetServersInEnvironment("production").Select(s => s.Name)
            .Should().BeEquivalentTo("srv-prod-01", "srv-prod-02");
        config.GetServersInEnvironment("ref").Select(s => s.Name).Should().Equal("srv-ref-01");
        config.GetServersInEnvironment("unknown").Should().BeEmpty();
    }

    [Fact]
    public void GetDomainsInEnvironment_AndForServer()
    {
        var config = SampleConfig();
        config.Domains.Add(new Domain { Name = "app-services-prod", Environment = "production", Servers = { "srv-prod-02" } });
        config.Domains.Add(new Domain { Name = "app-services", Environment = "Production", Servers = { "srv-prod-01", "srv-prod-02" } });
        config.Domains.Add(new Domain { Name = "app-services-dev", Environment = "development", Servers = { "srv-dev-01" } });

        config.GetDomainsInEnvironment("production").Select(d => d.Name)
            .Should().Equal("app-services", "app-services-prod");
        config.GetDomainsForServer("SRV-PROD-02").Should().HaveCount(2);
        config.GetDomainsForServer("srv-ref-01").Should().BeEmpty();
    }

    [Fact]
    public void RenderDomainList_ShowsDomainsOfEnvironmentEncoded()
    {
        var config = SampleConfig();
        config.Domains.Add(new Domain { Name = "app-services", Environment = "production", Servers = { "srv-prod-01" }, NetScalerVServer = "vs<1>" });
        config.Domains.Add(new Domain { Name = "app-services-dev", Environment = "development", Servers = { "srv-dev-01" } });

        var html = StaticHtmlGenerator.RenderDomainList(config, "production", "");

        html.Should().Contain("app-services").And.Contain("srv-prod-01").And.Contain("vs&lt;1&gt;");
        html.Should().NotContain("app-services-dev");
        StaticHtmlGenerator.RenderDomainList(config, "ref", "").Should().BeEmpty();
    }
}

public class DomainConfigurationServiceTests
{
    private static ConfigurationService CreateService()
    {
        var service = new ConfigurationService(new ConfigurationBuilder().Build());
        service.CreateNew();
        service.Config.Environments["production"] = new EnvironmentConfig { Services = { "srv-prod-01", "srv-prod-02" } };
        service.Config.Environments["development"] = new EnvironmentConfig { Services = { "srv-dev-01" } };
        foreach (var name in new[] { "srv-prod-01", "srv-prod-02", "srv-dev-01" })
            service.AddServer(new Server { Name = name, Active = true });
        return service;
    }

    [Fact]
    public void AddDomain_DuplicateNameCaseInsensitive_Throws()
    {
        var svc = CreateService();
        svc.AddDomain(new Domain { Name = "app-services", Environment = "production" });

        var act = () => svc.AddDomain(new Domain { Name = "APP-SERVICES", Environment = "production" });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BothProdVariants_AreIndependentEntries()
    {
        var svc = CreateService();
        svc.AddDomain(new Domain { Name = "app-services", Environment = "production", Servers = { "srv-prod-01" } });
        svc.AddDomain(new Domain { Name = "app-services-prod", Environment = "production", Servers = { "srv-prod-02" } });

        svc.GetDomains().Should().HaveCount(2);
        svc.GetDomain("app-services")!.Servers.Should().Equal("srv-prod-01");
        svc.GetDomain("app-services-prod")!.Servers.Should().Equal("srv-prod-02");
    }

    [Fact]
    public void RenameServer_UpdatesDomainServers_DeleteServer_RemovesThem()
    {
        var svc = CreateService();
        svc.AddDomain(new Domain { Name = "app-services", Environment = "production", Servers = { "srv-prod-01" } });

        svc.UpdateServer("srv-prod-01", new Server { Name = "srv-prod-11", Active = true });
        svc.GetDomain("app-services")!.Servers.Should().Equal("srv-prod-11");

        svc.DeleteServer("srv-prod-11");
        svc.GetDomain("app-services")!.Servers.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDomain_Rename_And_Delete()
    {
        var svc = CreateService();
        svc.AddDomain(new Domain { Name = "app", Environment = "production" });

        svc.UpdateDomain("app", new Domain { Name = "app-prod", Environment = "production" });
        svc.GetDomain("app").Should().BeNull();
        svc.GetDomain("app-prod").Should().NotBeNull();

        svc.DeleteDomain("APP-PROD");
        svc.GetDomains().Should().BeEmpty();
    }

    [Fact]
    public void GetServerNamesInEnvironment_ReturnsOnlyThatEnvironment()
    {
        var svc = CreateService();
        svc.GetServerNamesInEnvironment("production").Should().Equal("srv-prod-01", "srv-prod-02");
        svc.GetServerNamesInEnvironment("development").Should().Equal("srv-dev-01");
    }

    [Fact]
    public void LoadFromJson_WithoutDomains_HasNoChanges()
    {
        var svc = new ConfigurationService(new ConfigurationBuilder().Build());
        svc.LoadFromJson("""{ "servers": [ { "name": "a", "active": true } ] }""");

        svc.Config.Domains.Should().BeEmpty();
        svc.HasChanges.Should().BeFalse();
    }

    // ── Controller validation ───────────────────────────────────────────────

    [Fact]
    public void Validate_ServerOutsideEnvironment_AddsError()
    {
        var controller = new DomainsController(CreateService());

        controller.Validate(new DomainViewModel { Name = "app-dev", Environment = "development", Servers = { "srv-prod-01" } });

        controller.ModelState.ContainsKey(nameof(DomainViewModel.Servers)).Should().BeTrue();
    }

    [Fact]
    public void Validate_Mismatch_RequiresException()
    {
        var controller = new DomainsController(CreateService());

        controller.Validate(new DomainViewModel { Name = "app-dev", Environment = "production" });
        controller.ModelState.ContainsKey(nameof(DomainViewModel.AllowNameMismatch)).Should().BeTrue();

        var ok = new DomainsController(CreateService());
        ok.Validate(new DomainViewModel { Name = "app-dev", Environment = "production", AllowNameMismatch = true });
        ok.ModelState.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NormalisesNameEnvironmentAndServerCasing()
    {
        var controller = new DomainsController(CreateService());

        var domain = controller.Validate(new DomainViewModel
        {
            Name = " App-Services.Example.org. ",
            Environment = "PRODUCTION",
            Servers = { "SRV-PROD-01", "srv-prod-01" }
        });

        controller.ModelState.IsValid.Should().BeTrue();
        domain.Name.Should().Be("app-services.example.org");
        domain.Environment.Should().Be("production");
        domain.Servers.Should().Equal("srv-prod-01");
    }

    [Fact]
    public void Validate_UnknownEnvironment_AddsError()
    {
        var controller = new DomainsController(CreateService());
        controller.Validate(new DomainViewModel { Name = "app", Environment = "staging" });
        controller.ModelState.ContainsKey(nameof(DomainViewModel.Environment)).Should().BeTrue();
    }

    [Fact]
    public void Validate_DuplicateName_OnCreate_AddsError()
    {
        var svc = CreateService();
        svc.AddDomain(new Domain { Name = "app", Environment = "production" });
        var controller = new DomainsController(svc);

        controller.Validate(new DomainViewModel { Name = "APP", Environment = "production" });
        controller.ModelState.ContainsKey(nameof(DomainViewModel.Name)).Should().BeTrue();

        var edit = new DomainsController(svc);
        edit.Validate(new DomainViewModel { OriginalName = "app", Name = "app", Environment = "production" });
        edit.ModelState.IsValid.Should().BeTrue();
    }
}
