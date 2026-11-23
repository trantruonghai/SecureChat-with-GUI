using System.Collections.Concurrent;
using SecureChat.Client.Security;

namespace SecureChat.Client.Models;

/// <summary>Trạng thái cục bộ của client (Local State).</summary>
public class ClientState
{
    public string? Username { get; set; }
    public RsaKeyPair? Keys { get; set; }
    public string? CurrentPartner { get; set; }

    /// <summary>username -> đang online?</summary>
    public ConcurrentDictionary<string, bool> Online { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoggedIn => Username != null;

    public void Reset()
    {
        Username = null;
        Keys = null;
        CurrentPartner = null;
        Online.Clear();
    }
}

public record HistoryItem(string Sender, string Receiver, string Text, DateTime Timestamp, bool Decrypted);
