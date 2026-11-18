using System.Security.Cryptography;

namespace MessageBus.Crypto;

/// <summary>The private keys a machine holds, by key id (<c>ek_…</c>): its own device key and the role and workspace keys granted to it.</summary>
public interface IKeyRing
{
    /// <summary>The private key for <paramref name="keyId"/>, or null when this machine doesn't hold it.</summary>
    /// <param name="keyId">A key id from <see cref="Contracts.Messaging.EncryptionInfo.KeyId"/>.</param>
    KeyPair? Find(string keyId);
}

/// <summary>An in-memory <see cref="IKeyRing"/>. It owns (and disposes) the keys added to it.</summary>
public sealed class KeyRing : IKeyRing, IDisposable
{
    private readonly Dictionary<string, KeyPair> _keys = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The key ids held.</summary>
    public IReadOnlyCollection<string> KeyIds
    {
        get { lock (_gate) return [.. _keys.Keys]; }
    }

    /// <summary>Adds (or replaces) the private key for <paramref name="keyId"/>.</summary>
    /// <param name="keyId">The key's id.</param>
    /// <param name="key">The private key; the ring disposes it.</param>
    public KeyRing Add(string keyId, KeyPair key)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_keys.Remove(keyId, out var old) && !ReferenceEquals(old, key)) old.Dispose();
            _keys[keyId] = key;
        }
        return this;
    }

    /// <summary>Forgets a key (for example one whose grant was revoked or that expired).</summary>
    /// <param name="keyId">The key's id.</param>
    public bool Remove(string keyId)
    {
        lock (_gate)
        {
            if (!_keys.Remove(keyId, out var key)) return false;
            key.Dispose();
            return true;
        }
    }

    /// <inheritdoc />
    public KeyPair? Find(string keyId)
    {
        lock (_gate) return _keys.GetValueOrDefault(keyId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _keys.Values) key.Dispose();
            _keys.Clear();
        }
    }
}

/// <summary>Decryption needs a key this machine doesn't hold (no grant for that role key version, or it expired).</summary>
public sealed class MissingKeyException(string keyId)
    : CryptographicException($"This machine doesn't hold key {keyId}. It needs a key grant for that role (or workspace) key version.")
{
    /// <summary>The key id that was needed.</summary>
    public string KeyId { get; } = keyId;
}
