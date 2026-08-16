using PassCrate.Core.Models;

namespace PassCrate.Tests.Sync;

public sealed class CloudPassphraseModelTests
{
    [Fact]
    public void RestoreRequestUsesOnlyVaultPassphrase()
    {
        var request = new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = "vault",
            VaultPassphrase = "correct horse battery staple 2026",
        };
        Assert.Equal("correct horse battery staple 2026", request.VaultPassphrase);
    }
}
