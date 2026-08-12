using ErsatzTV.Core.Next;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Next;

/// <summary>
///     The version is what a worker checks before it reads anything else, so these pin the literal
///     uris rather than the constants that produce them.
/// </summary>
[TestFixture]
public class PlayoutSchemaVersionTests
{
    [Test]
    public void A_Document_Carrying_A_Slate_Should_Declare_The_Version_Slate_Arrived_In()
    {
        // declaring less would be read by a worker that does not know the key, which drops the
        // slate and airs the templated source the schedule stood down from
        string version = PlayoutSchemaVersion.For(
        [
            new PlayoutItem(),
            new PlayoutItem
            {
                Slate = new Source { SourceType = SourceType.Local, Path = "/bumps/WeatherSlate.mp4" }
            }
        ]);

        version.ShouldBe("https://ersatztv.org/playout/version/0.0.3");
    }

    [Test]
    public void A_Document_Without_A_Slate_Should_Declare_What_It_Always_Declared()
    {
        // channels that never schedule a slate keep working with the worker they already run
        string version = PlayoutSchemaVersion.For([new PlayoutItem(), new PlayoutItem()]);

        version.ShouldBe("https://ersatztv.org/playout/version/0.0.2");
    }

    [Test]
    public void An_Empty_Document_Should_Declare_The_Floor()
    {
        PlayoutSchemaVersion.For([]).ShouldBe("https://ersatztv.org/playout/version/0.0.2");
    }
}
