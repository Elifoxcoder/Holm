using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNotch.Sharing;

/// <summary>One member of a group chat (enough to send sealed notes to their inbox).</summary>
public sealed class GroupMember
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public string? Server { get; set; }

    public Friend ToFriend() => new() { Key = Key, Name = Name, Topic = Topic, Server = Server };

    public static GroupMember FromFriend(Friend f) => new() { Key = f.Key, Name = f.Name, Topic = f.Topic, Server = f.Server };
}

/// <summary>An end-to-end encrypted group chat shared among friends.</summary>
public sealed class ChatGroup
{
    public const int MaxName = 40;
    public const int MaxMembers = 16;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    /// <summary>32-byte symmetric AES-256 group key, base64url encoded.</summary>
    public string Key { get; set; } = "";
    public int KeyEpoch { get; set; } = 1;
    public string CreatorKey { get; set; } = "";
    public List<GroupMember> Members { get; set; } = new();
    public long Created { get; set; }

    public static string? CleanName(string? name) => FriendProfile.CleanText(name, MaxName);
}

/// <summary>
/// End-to-end encryption helpers for 1:1 chats (safety numbers), group chats (HKDF-SHA256 + AES-256-GCM per message),
/// and encrypted local chat storage at rest.
/// </summary>
public static class ChatCrypto
{
    const int NonceSize = 12, TagSize = 16, KeySize = 32;
    public const string GroupCipherPrefix = "e2e1:";
    public const string StorageHeader = "HNC1\n";

    /// <summary>Generates a fresh random 256-bit group key (base64url).</summary>
    public static string NewGroupKey() => FriendIdentity.Base64Url(RandomNumberGenerator.GetBytes(KeySize));

    public static bool TryDecodeGroupKey(string? base64Url, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(base64Url) || base64Url.Length > 64) return false;
        try
        {
            byte[] raw = FriendIdentity.FromBase64Url(base64Url);
            if (raw.Length != KeySize) return false;
            key = raw;
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Derives a 32-byte per-message AES-256-GCM key from the group key and message id.</summary>
    static byte[] DeriveGroupMessageKey(byte[] groupKey, string groupId, string messageId)
    {
        byte[] salt = Encoding.UTF8.GetBytes(messageId);
        byte[] info = Encoding.UTF8.GetBytes("HolyNotch-GroupChat-v1|" + groupId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, groupKey, KeySize, salt, info);
    }

    /// <summary>Encrypts a group chat message with AES-256-GCM, authenticating groupId, senderKey, and messageId.</summary>
    public static string EncryptGroupMessage(byte[] groupKey, string groupId, string senderKey, string messageId, string plaintext)
    {
        if (groupKey.Length != KeySize) throw new ArgumentException("Group key must be 32 bytes.", nameof(groupKey));
        byte[] msgKey = DeriveGroupMessageKey(groupKey, groupId, messageId);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] aad = Encoding.UTF8.GetBytes($"{groupId}|{senderKey}|{messageId}");
        var box = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(box.AsSpan(0, NonceSize));
        try
        {
            using var aes = new AesGcm(msgKey, TagSize);
            aes.Encrypt(box.AsSpan(0, NonceSize), plain, box.AsSpan(NonceSize + TagSize), box.AsSpan(NonceSize, TagSize), aad);
            return GroupCipherPrefix + FriendIdentity.Base64Url(box);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(msgKey);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts a group chat message; returns null if tampered, wrong key, or malformed.</summary>
    public static string? DecryptGroupMessage(byte[] groupKey, string groupId, string senderKey, string messageId, string payload)
    {
        if (groupKey.Length != KeySize || !payload.StartsWith(GroupCipherPrefix, StringComparison.Ordinal)) return null;
        byte[] msgKey = DeriveGroupMessageKey(groupKey, groupId, messageId);
        try
        {
            byte[] box = FriendIdentity.FromBase64Url(payload[GroupCipherPrefix.Length..]);
            if (box.Length < NonceSize + TagSize) return null;
            var plain = new byte[box.Length - NonceSize - TagSize];
            byte[] aad = Encoding.UTF8.GetBytes($"{groupId}|{senderKey}|{messageId}");
            using var aes = new AesGcm(msgKey, TagSize);
            aes.Decrypt(box.AsSpan(0, NonceSize), box.AsSpan(NonceSize + TagSize), box.AsSpan(NonceSize, TagSize), plain, aad);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(msgKey);
        }
    }

    /// <summary>
    /// Deterministic 60-digit safety number (12 groups of 5 digits) for a 1:1 conversation.
    /// Both sides compute the exact same number regardless of order.
    /// </summary>
    public static string SafetyNumber(string keyA, string keyB)
    {
        string first = string.CompareOrdinal(keyA, keyB) <= 0 ? keyA : keyB;
        string second = string.CompareOrdinal(keyA, keyB) <= 0 ? keyB : keyA;
        byte[] hash1 = SHA256.HashData(Encoding.UTF8.GetBytes($"HolyNotch-E2E-v1|{first}|{second}"));
        byte[] hash2 = SHA256.HashData(hash1);
        var groups = new List<string>(12);
        for (int i = 0; i < 6; i++)
            groups.Add(FiveDigits(hash1, i * 4));
        for (int i = 0; i < 6; i++)
            groups.Add(FiveDigits(hash2, i * 4));
        return string.Join(" ", groups.Take(6)) + "\n" + string.Join(" ", groups.Skip(6));
    }

    /// <summary>Deterministic 30-digit fingerprint (6 groups of 5 digits) for an E2E group key.</summary>
    public static string GroupFingerprint(byte[] groupKey, string groupId)
    {
        using var sha = SHA256.Create();
        byte[] prefix = Encoding.UTF8.GetBytes($"HolyNotch-GroupFP-v1|{groupId}|");
        sha.TransformBlock(prefix, 0, prefix.Length, null, 0);
        sha.TransformFinalBlock(groupKey, 0, groupKey.Length);
        byte[] hash = sha.Hash!;
        return string.Join(" ", Enumerable.Range(0, 6).Select(i => FiveDigits(hash, i * 4)));
    }

    static string FiveDigits(byte[] hash, int offset)
    {
        uint val = (uint)(hash[offset] << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3]);
        return (val % 100_000).ToString("D5", CultureInfo.InvariantCulture);
    }

    /// <summary>Derives a 32-byte local chat storage encryption key from the user's P-256 identity private key.</summary>
    public static byte[] DeriveStorageKey(FriendIdentity identity)
    {
        byte[] pkcs8 = identity.Key.ExportPkcs8PrivateKey();
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, pkcs8, KeySize,
                salt: Encoding.UTF8.GetBytes(identity.Topic),
                info: "HolyNotch-ChatStore-v1"u8.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>Encrypts local chat JSON with AES-256-GCM for storage on disk.</summary>
    public static string EncryptStorage(byte[] storageKey, string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json);
        var box = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(box.AsSpan(0, NonceSize));
        try
        {
            using var aes = new AesGcm(storageKey, TagSize);
            aes.Encrypt(box.AsSpan(0, NonceSize), plain, box.AsSpan(NonceSize + TagSize), box.AsSpan(NonceSize, TagSize),
                "HolyNotch-ChatStore-v1"u8);
            return StorageHeader + Convert.ToBase64String(box);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts local chat JSON stored with <see cref="EncryptStorage"/>; returns null on failure.</summary>
    public static string? DecryptStorage(byte[] storageKey, string content)
    {
        if (!content.StartsWith(StorageHeader, StringComparison.Ordinal)) return null;
        try
        {
            byte[] box = Convert.FromBase64String(content[StorageHeader.Length..].Trim());
            if (box.Length < NonceSize + TagSize) return null;
            var plain = new byte[box.Length - NonceSize - TagSize];
            using var aes = new AesGcm(storageKey, TagSize);
            aes.Decrypt(box.AsSpan(0, NonceSize), box.AsSpan(NonceSize + TagSize), box.AsSpan(NonceSize, TagSize), plain,
                "HolyNotch-ChatStore-v1"u8);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Wire payload for creating/updating a group via pairwise E2E sealed notes.</summary>
public sealed class GroupInvitePayload
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("c")] public string? Color { get; set; }
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    [JsonPropertyName("e")] public int Epoch { get; set; } = 1;
    [JsonPropertyName("r")] public string Creator { get; set; } = "";
    [JsonPropertyName("m")] public List<MemberDto> Members { get; set; } = new();

    public sealed class MemberDto
    {
        [JsonPropertyName("k")] public string Key { get; set; } = "";
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("t")] public string Topic { get; set; } = "";
        [JsonPropertyName("s")] public string? Server { get; set; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ChatGroup? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<GroupInvitePayload>(json, Json);
            if (dto == null || !Guid.TryParseExact(dto.Id, "N", out _)) return null;
            if (ChatGroup.CleanName(dto.Name) is not { } cleanName) return null;
            if (!ChatCrypto.TryDecodeGroupKey(dto.Key, out _)) return null;
            if (dto.Members == null || dto.Members.Count is < 2 or > ChatGroup.MaxMembers) return null;
            var members = new List<GroupMember>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in dto.Members)
            {
                var card = new Friend { Key = m.Key, Name = m.Name, Topic = m.Topic, Server = m.Server };
                if (!FriendIdentity.IsValidFriend(card) || !seen.Add(card.Key)) continue;
                members.Add(new GroupMember
                {
                    Key = card.Key,
                    Name = FriendProfile.CleanText(card.Name, FriendProfile.MaxName) ?? "Member",
                    Topic = card.Topic,
                    Server = card.Server,
                });
            }
            if (members.Count < 2) return null;
            return new ChatGroup
            {
                Id = dto.Id,
                Name = cleanName,
                Color = FriendProfile.CleanColor(dto.Color),
                Key = dto.Key,
                KeyEpoch = Math.Max(1, dto.Epoch),
                CreatorKey = dto.Creator,
                Members = members,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
        catch (JsonException) { return null; }
    }

    public static GroupInvitePayload FromGroup(ChatGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Color = group.Color,
        Key = group.Key,
        Epoch = group.KeyEpoch,
        Creator = group.CreatorKey,
        Members = group.Members.Select(m => new MemberDto { Key = m.Key, Name = m.Name, Topic = m.Topic, Server = m.Server }).ToList(),
    };
}
