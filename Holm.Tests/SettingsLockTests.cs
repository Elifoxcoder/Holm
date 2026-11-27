using WinNotch;
using Xunit;

namespace WinNotch.Tests;

public sealed class SettingsLockTests
{
    [Fact]
    public void Hash_verifies_matching_password_and_rejects_wrong_ones()
    {
        string hash = SettingsLock.Hash("Secret123!");
        Assert.StartsWith("v1:", hash);
        Assert.True(SettingsLock.Verify(hash, "Secret123!"));
        Assert.False(SettingsLock.Verify(hash, "secret123!"));
        Assert.False(SettingsLock.Verify(hash, ""));
        Assert.False(SettingsLock.Verify(hash, null));
    }

    [Fact]
    public void Hash_uses_unique_random_salts()
    {
        string a = SettingsLock.Hash("same-password");
        string b = SettingsLock.Hash("same-password");
        Assert.NotEqual(a, b);
        Assert.True(SettingsLock.Verify(a, "same-password"));
        Assert.True(SettingsLock.Verify(b, "same-password"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1:not-valid")]
    [InlineData("v2:aaaa:bbbb")]
    [InlineData("v1:!!!:!!!")]
    public void Malformed_hashes_never_verify(string? stored)
    {
        Assert.False(SettingsLock.Verify(stored, "anything"));
    }
}
