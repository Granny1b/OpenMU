using Xunit;

namespace MuSite.Tests;

/// <summary>
/// The site hashes registration passwords; the GAME verifies them. If the two disagree, the account
/// is created and can never log in, with no error anywhere. This pins the contract.
///
/// OpenMU hashes with BCrypt.Net.BCrypt.HashPassword(password) at the library default work factor
/// (VersionSeasonSix/TestAccounts/AccountInitializerBase.cs:92) and verifies with BCrypt.Verify
/// (src/Persistence/EntityFramework/AccountRepository.cs:125). The package version is pinned to
/// 4.0.3 in both website/Directory.Packages.props and src/Directory.Packages.props.
/// </summary>
public class BCryptCompatibilityTests
{
    [Fact]
    public void HashRoundTripsThroughVerify()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("correct horse");

        Assert.True(BCrypt.Net.BCrypt.Verify("correct horse", hash));
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong horse", hash));
    }

    [Fact]
    public void DefaultHashIsAWellFormedBCryptHashWithASaneCost()
    {
        // Structural, not a hardcoded prefix: the point is to notice if the pinned package ever
        // changes its defaults to something weaker or to a version marker the game rejects.
        var hash = BCrypt.Net.BCrypt.HashPassword("anything");

        var match = System.Text.RegularExpressions.Regex.Match(hash, @"^\$2[aby]\$(\d{2})\$");
        Assert.True(match.Success, $"unexpected hash format: {hash[..Math.Min(7, hash.Length)]}");
        Assert.True(int.Parse(match.Groups[1].Value) >= 10, "work factor dropped below 10");
    }

    [Fact]
    public void VerifyAcceptsAHashProducedWithTheGamesCall()
    {
        // Exactly the call at AccountInitializerBase.cs:92.
        var gameHash = BCrypt.Net.BCrypt.HashPassword("test1");

        Assert.True(BCrypt.Net.BCrypt.Verify("test1", gameHash));
    }
}
