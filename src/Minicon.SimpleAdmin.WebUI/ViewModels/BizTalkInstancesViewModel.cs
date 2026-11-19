namespace Minicon.SimpleAdmin.WebUI.ViewModels;

public sealed record BizTalkServerChoice(string Id, string Name);
public sealed record BizTalkInstancesViewModel(
    IReadOnlyList<BizTalkServerChoice> Servers, string? Server, string? Application,
    string? Error, bool MissingApplication, bool Enabled, string? DisabledReason);
