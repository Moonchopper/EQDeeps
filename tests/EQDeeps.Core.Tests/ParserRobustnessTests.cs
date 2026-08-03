using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Sessions;
using EQDeeps.TestSupport;
using Xunit;

namespace EQDeeps.Core.Tests;

/// <summary>
/// Grammars whose fixed prefix and suffix meet with nothing between them.
///
/// These lines are real and common — EverQuest omits the spell name when it
/// never got far enough to have one — and the slice that pulled the name out
/// ran backwards, throwing. The throw unwound the ingestion task, completed the
/// batch channel, and stopped the session dead partway through the file with
/// nothing reported anywhere: a parse that just ended early and looked fine.
/// </summary>
public class ParserRobustnessTests
{
    private static readonly ParserOptions Options = new("Kizant");

    private static GameEvent? Parse(string line) => new LogEventParser(Options).Parse(line);

    [Fact]
    public void NamelessInterruptParsesWithoutASpell()
    {
        var cast = Assert.IsType<CastEvent>(Parse("Your spell is interrupted."));
        Assert.Equal(CastKind.Interrupted, cast.Kind);
        Assert.Equal("Kizant", cast.Caster);
        Assert.Null(cast.Spell);
    }

    [Fact]
    public void NamedInterruptStillCarriesItsSpell()
    {
        var cast = Assert.IsType<CastEvent>(Parse("Your Burst of Flames spell is interrupted."));
        Assert.Equal("Burst of Flames", cast.Spell);
    }

    /// <summary>The same shape in two more grammars; neither may throw.</summary>
    [Theory]
    [InlineData("Your target resisted the spell.")]
    [InlineData("Soandso is focused on attacking due to an improved taunt.")]
    [InlineData(" is focused on attacking due to an improved taunt.")]
    public void DegenerateVariantsAreDeclinedNotThrown(string line)
    {
        Parse(line); // must not throw; a null result is a fine answer
    }

    /// <summary>
    /// The safety net behind those fixes: whatever a grammar does, a session
    /// reads the file to the end. This drives the real ingestion pipeline,
    /// because the failure being guarded against was ingestion stopping — not
    /// a parser returning the wrong record.
    /// </summary>
    [Fact]
    public async Task OneBadLineCannotStopIngestion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqdeeps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "eqlog_Kizant_xegony.txt");
            var t0 = new DateTime(2024, 3, 9, 20, 0, 0);
            File.WriteAllLines(path,
            [
                SyntheticLogGenerator.Prefix(t0) + "You crush an ice giant for 100 points of damage.",
                SyntheticLogGenerator.Prefix(t0.AddSeconds(1)) + "Your spell is interrupted.",
                SyntheticLogGenerator.Prefix(t0.AddSeconds(2)) + "You crush an ice giant for 200 points of damage.",
            ]);

            var session = new Session(path, ingestOptions: new IngestOptions { Follow = false });
            await session.RunAsync(CancellationToken.None);

            Assert.Equal(0, session.ParserFailures);
            // The line after the hazard is the one that matters: it proves the
            // reader kept going rather than the file merely being short.
            Assert.Equal(3, session.Records.Count);
            Assert.True(session.BackfillComplete);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
