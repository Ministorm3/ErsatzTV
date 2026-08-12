using ErsatzTV.Core.Domain;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Domain;

[TestFixture]
public class PlayoutItemTests
{
    [Test]
    public void GetDisplayDuration_ShortDuration()
    {
        var item = new PlayoutItem
        {
            Start = DateTime.UtcNow.Date,
            Finish = DateTime.UtcNow.Date.AddHours(3).AddMinutes(5).AddSeconds(4)
        };

        string actual = item.GetDisplayDuration();

        actual.ShouldBe("3:05:04");
    }

    [Test]
    public void GetDisplayDuration_LongDuration()
    {
        var item = new PlayoutItem
        {
            Start = DateTime.UtcNow.Date,
            Finish = DateTime.UtcNow.Date.AddHours(27).AddMinutes(5).AddSeconds(4)
        };

        string actual = item.GetDisplayDuration();

        actual.ShouldBe("27:05:04");
    }

    [Test]
    public void Clone_Should_Carry_The_Slate_Id_And_Not_Its_Entity()
    {
        // a clone is on its way into the database, and the navigations stay behind for the same
        // reason MediaItem does: a loaded entity travelling on an insert takes its graph with it
        var item = new PlayoutItem
        {
            MediaItemId = 1,
            MediaItem = new Movie { Id = 1 },
            SlateMediaItemId = 2,
            SlateMediaItem = new Movie { Id = 2 },
            Start = DateTime.UtcNow.Date,
            Finish = DateTime.UtcNow.Date.AddHours(1)
        };

        PlayoutItem clone = item.Clone();

        clone.SlateMediaItemId.ShouldBe(2);
        clone.SlateMediaItem.ShouldBeNull();
        clone.MediaItem.ShouldBeNull();
    }
}
