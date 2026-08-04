using ErsatzTV.Controllers;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Controllers;

/// <summary>
///     A parameter dropped here is invisible: the stream keeps working and simply never varies by
///     cohort, which is exactly how per-cohort streams failed before the query was forwarded.
/// </summary>
[TestFixture]
public class IptvControllerTests
{
    private static IQueryCollection Query(string queryString) =>
        new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString));

    [Test]
    public void NextPlaylistQuery_ShouldBeEmpty_WhenNothingWasAsked()
    {
        IptvController.NextPlaylistQuery(Query(string.Empty)).ShouldBeEmpty();
    }

    [Test]
    public void NextPlaylistQuery_ShouldKeepTheAccessToken()
    {
        IptvController.NextPlaylistQuery(Query("?access_token=abc")).ShouldBe("?access_token=abc");
    }

    [Test]
    public void NextPlaylistQuery_ShouldForwardACustomParameter()
    {
        IptvController.NextPlaylistQuery(Query("?zip=15216")).ShouldBe("?zip=15216");
    }

    [Test]
    public void NextPlaylistQuery_ShouldCarryBothTheTokenAndTheCustomParameter()
    {
        IptvController.NextPlaylistQuery(Query("?access_token=abc&zip=15216"))
            .ShouldBe("?access_token=abc&zip=15216");
    }

    /// <summary>
    ///     mode and index steer streaming itself and would only add noise for the worker to
    ///     discard; access_token is emitted separately rather than twice.
    /// </summary>
    [Test]
    public void NextPlaylistQuery_ShouldDropParametersStreamingConsumes()
    {
        IptvController.NextPlaylistQuery(Query("?mode=segmenter&index=3&zip=15216"))
            .ShouldBe("?zip=15216");
    }

    [Test]
    public void NextPlaylistQuery_ShouldNotRepeatTheAccessToken()
    {
        IptvController.NextPlaylistQuery(Query("?access_token=abc"))
            .Split("access_token").Length.ShouldBe(2);
    }

    [Test]
    public void NextPlaylistQuery_ShouldEscapeForwardedValues()
    {
        IptvController.NextPlaylistQuery(Query("?city=New%20York%26x%3D1"))
            .ShouldBe("?city=New%20York%26x%3D1");
    }

    [Test]
    public void ForwardedQueryParameters_ShouldBeEmpty_WhenOnlyStreamingParametersWereAsked()
    {
        IptvController.ForwardedQueryParameters(Query("?mode=segmenter&access_token=abc"))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     The access token is added separately by whatever builds the url, so including it here
    ///     would emit it twice.
    /// </summary>
    [Test]
    public void ForwardedQueryParameters_ShouldCarryNoLeadingSeparatorAndNoAccessToken()
    {
        IptvController.ForwardedQueryParameters(Query("?access_token=abc&zip=15216&lang=en"))
            .ShouldBe("zip=15216&lang=en");
    }
}
