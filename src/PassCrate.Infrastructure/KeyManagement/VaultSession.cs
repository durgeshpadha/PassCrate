using System.Security.Cryptography;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.KeyManagement;

public sealed class VaultSession : IVaultSession, IDisposable
{
    private readonly object _sync = new();
    private byte[]? _dataEncryptionKey;

    public bool IsUnlocked
    {
        get
        {
            lock (_sync)
            {
                return _dataEncryptionKey is not null;
            }
        }
    }

    public T UseKey<T>(Func<ReadOnlyMemory<byte>, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        byte[] keyCopy;
        lock (_sync)
        {
            if (_dataEncryptionKey is null)
            {
                throw new VaultLockedException();
            }

            keyCopy = _dataEncryptionKey.ToArray();
        }

        try
        {
            return operation(keyCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
        }
    }

    public void Unlock(ReadOnlySpan<byte> dataEncryptionKey)
    {
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
        {
            throw new ArgumentException("The data encryption key must be 256 bits.", nameof(dataEncryptionKey));
        }

        lock (_sync)
        {
            ClearKey();
            _dataEncryptionKey = dataEncryptionKey.ToArray();
        }
    }

    public void Lock()
    {
        lock (_sync)
        {
            ClearKey();
        }
    }

    public void Dispose() => Lock();

    private void ClearKey()
    {
        if (_dataEncryptionKey is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_dataEncryptionKey);
        _dataEncryptionKey = null;
    }
}

