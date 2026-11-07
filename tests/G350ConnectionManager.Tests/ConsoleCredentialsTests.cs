using G350ConnectionManager.Models;
using Xunit;

namespace G350ConnectionManager.Tests;

/// <summary>
/// Credential resolution. Every console service reads through this type, so a
/// wrong fallback means every feature fails to connect for a reason that looks
/// like the console is offline.
/// </summary>
public class ConsoleCredentialsTests
{
    [Fact]
    public void Apply_WithBlankValues_KeepsTheArkOsDefaults()
    {
        ConsoleCredentials.Apply(null, null);

        Assert.Equal(ConsoleCredentials.DefaultUser, ConsoleCredentials.User);
        Assert.Equal(ConsoleCredentials.DefaultPassword, ConsoleCredentials.Password);
    }

    [Fact]
    public void Apply_WithEmptyStrings_KeepsTheArkOsDefaults()
    {
        ConsoleCredentials.Apply("", "");

        Assert.Equal("ark", ConsoleCredentials.User);
        Assert.Equal("ark", ConsoleCredentials.Password);
    }

    [Fact]
    public void Apply_WithWhitespace_KeepsTheArkOsDefaults()
    {
        ConsoleCredentials.Apply("   ", "");

        Assert.Equal("ark", ConsoleCredentials.User);
    }

    [Fact]
    public void Apply_UsesConfiguredValues()
    {
        ConsoleCredentials.Apply("player", "secret");

        Assert.Equal("player", ConsoleCredentials.User);
        Assert.Equal("secret", ConsoleCredentials.Password);
    }

    [Fact]
    public void Apply_TrimsTheUserButNotThePassword()
    {
        // A trailing space in a password is legal and must survive; a padded
        // username is always a copy-paste artefact.
        ConsoleCredentials.Apply("  player  ", "secret ");

        Assert.Equal("player", ConsoleCredentials.User);
        Assert.Equal("secret ", ConsoleCredentials.Password);
    }

    [Fact]
    public void Apply_AllowsAChangedUserWithTheDefaultPassword()
    {
        ConsoleCredentials.Apply("player", "");

        Assert.Equal("player", ConsoleCredentials.User);
        Assert.Equal(ConsoleCredentials.DefaultPassword, ConsoleCredentials.Password);
    }
}
