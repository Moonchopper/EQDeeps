using EQDeeps.Core.Sessions;
using Xunit;

namespace EQDeeps.Core.Tests;

public class IdentityRegistryTests
{
    private readonly IdentityRegistry _registry = new();

    [Fact]
    public void ArticleAndMultiWordNamesAreNpcs()
    {
        Assert.True(_registry.IsDefinitelyNpc("An abyssal terror"));
        Assert.True(_registry.IsDefinitelyNpc("a shadow drake"));
        Assert.True(_registry.IsDefinitelyNpc("The Fabled Wuoshi"));
        Assert.True(_registry.IsDefinitelyNpc("Grendish the Crusader"));
        Assert.False(_registry.IsDefinitelyNpc("Doomshade")); // single word: unknown
        Assert.Equal(EntityKind.Unknown, _registry.Classify("Doomshade"));
    }

    [Fact]
    public void VerificationBeatsNpcEvidenceInBothOrders()
    {
        _registry.AddKnownNpc("Falsehood");
        Assert.True(_registry.IsDefinitelyNpc("Falsehood"));

        _registry.AddVerifiedPlayer("Falsehood");
        Assert.False(_registry.IsDefinitelyNpc("Falsehood"));
        Assert.True(_registry.IsVerifiedPlayer("Falsehood"));

        _registry.AddKnownNpc("Falsehood"); // late NPC evidence is ignored
        Assert.False(_registry.IsDefinitelyNpc("Falsehood"));
    }

    [Fact]
    public void PlayerVerifiedFiresOncePerName()
    {
        var events = new List<string>();
        _registry.PlayerVerified += events.Add;
        _registry.AddVerifiedPlayer("Kizant");
        _registry.AddVerifiedPlayer("Kizant");
        _registry.AddVerifiedPlayer("Multi Word"); // not a player name shape

        Assert.Equal(["Kizant"], events);
    }

    [Fact]
    public void PossessivePetsResolveOwnersWithoutMappings()
    {
        Assert.Equal("Kizante", _registry.OwnerOf("Kizante`s pet"));
        Assert.Equal("Tolzol", _registry.OwnerOf("Tolzol's pet"));
        Assert.True(_registry.IsPlayerSide("Kizante`s pet"));
        Assert.False(_registry.IsDefinitelyNpc("Kizante`s pet"));
        Assert.Equal(EntityKind.Pet, _registry.Classify("Kizante`s pet"));

        // A pet possessed by an obvious NPC is not player-side.
        Assert.False(_registry.IsPlayerSide("a werewolf`s pet"));
    }

    [Fact]
    public void MappedPetsArePlayerSide()
    {
        Assert.Null(_registry.OwnerOf("Xobatik"));
        _registry.MapPetToOwner("Xobatik", "Piemastaj");
        Assert.Equal("Piemastaj", _registry.OwnerOf("Xobatik"));
        Assert.True(_registry.IsPlayerSide("Xobatik"));
        Assert.Equal(EntityKind.Pet, _registry.Classify("Xobatik"));
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        _registry.AddVerifiedPlayer("Kizant");
        _registry.AddKnownNpc("Doomshade");
        _registry.MapPetToOwner("Xobatik", "Kizant");

        var restored = IdentityRegistry.FromSnapshot(_registry.CreateSnapshot());
        Assert.True(restored.IsVerifiedPlayer("Kizant"));
        Assert.True(restored.IsDefinitelyNpc("Doomshade"));
        Assert.Equal("Kizant", restored.OwnerOf("Xobatik"));
    }
}

public class LogFileNamesTests
{
    [Theory]
    [InlineData(@"C:\EQ\Logs\eqlog_Kizant_xegony.txt", "Kizant", "xegony")]
    [InlineData("eqlog_Test_server.txt", "Test", "server")]
    [InlineData("eqlog_Soandso_firiona.txt.gz", "Soandso", "firiona")]
    [InlineData("eqlog_Emu_project_2002.txt", "Emu", "project_2002")]
    public void ParsesCharacterAndServer(string path, string character, string server)
    {
        Assert.True(LogFileNames.TryParse(path, out var actualCharacter, out var actualServer));
        Assert.Equal(character, actualCharacter);
        Assert.Equal(server, actualServer);
    }

    [Theory]
    [InlineData("combat.log")]
    [InlineData("eqlog_NoServer.txt")]
    [InlineData("eqlog_.txt")]
    public void RejectsNonMatchingNames(string path)
    {
        Assert.False(LogFileNames.TryParse(path, out _, out _));
    }
}
