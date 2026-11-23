using SecureChat.Client.Models;
using SecureChat.Client.Security;
using Xunit;

namespace SecureChat.Client.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_IsDeterministic_AndCaseInsensitiveOnUsername()
    {
        Assert.Equal(PasswordHasher.Hash("Alice", "pw123"), PasswordHasher.Hash("alice", "pw123"));
    }

    [Fact]
    public void Hash_DiffersByPasswordAndByUser()
    {
        Assert.NotEqual(PasswordHasher.Hash("alice", "pw123"), PasswordHasher.Hash("alice", "pw124"));
        Assert.NotEqual(PasswordHasher.Hash("alice", "pw123"), PasswordHasher.Hash("bob", "pw123"));
    }

    [Fact]
    public void Hash_DoesNotContainPassword_AndFitsServerLimit()
    {
        var hash = PasswordHasher.Hash("alice", "matkhau-bi-mat");
        Assert.DoesNotContain("matkhau", hash);
        Assert.True(hash.Length <= 200); // Server từ chối > 200 ký tự
    }

    [Theory]
    [InlineData("LOGIN", "LOGIN_RESPONSE")]
    [InlineData("GET_PUBLIC_KEY", "GET_PUBLIC_KEY_RESPONSE")]
    public void ResponseType_MatchesServerConvention(string request, string expected)
    {
        Assert.Equal(expected, PacketType.ResponseOf(request));
    }
}
