using System.Text.Json;
using PassCrate.Core.Legal;

namespace PassCrate.App.Services;

public interface ILegalAcceptanceStore
{
    LegalAcceptanceReceipt? GetReceipt();
    bool HasAcceptedCurrentVersions { get; }
    void AcceptCurrentVersions();
}

public sealed class LegalAcceptanceStore : ILegalAcceptanceStore
{
    private const string AcceptanceKey = "passcrate.legal.acceptance.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool HasAcceptedCurrentVersions => LegalAcceptancePolicy.IsCurrent(GetReceipt());

    public LegalAcceptanceReceipt? GetReceipt()
    {
        var json = Preferences.Default.Get(AcceptanceKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LegalAcceptanceReceipt>(json, JsonOptions);
        }
        catch (JsonException)
        {
            Preferences.Default.Remove(AcceptanceKey);
            return null;
        }
    }

    public void AcceptCurrentVersions()
    {
        var receipt = new LegalAcceptanceReceipt(
            LegalAcceptancePolicy.CurrentTermsVersion,
            LegalAcceptancePolicy.CurrentPrivacyVersion,
            AppInfo.Current.VersionString,
            DateTimeOffset.UtcNow);
        Preferences.Default.Set(AcceptanceKey, JsonSerializer.Serialize(receipt, JsonOptions));
    }
}
