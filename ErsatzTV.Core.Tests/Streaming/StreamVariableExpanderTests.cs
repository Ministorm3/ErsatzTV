using ErsatzTV.Core.Streaming;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Streaming;

[TestFixture]
public class StreamVariableExpanderTests
{
    [TestFixture]
    public class HasVariables
    {
        [Test]
        public void Should_Be_False_For_Plain_Url()
        {
            StreamVariableExpander.HasVariables("http://localhost:8000/stream.ts").ShouldBeFalse();
        }

        [Test]
        public void Should_Be_False_For_Null_Or_Empty()
        {
            StreamVariableExpander.HasVariables(null).ShouldBeFalse();
            StreamVariableExpander.HasVariables(string.Empty).ShouldBeFalse();
        }

        [Test]
        public void Should_Be_False_For_Unknown_Braced_Content()
        {
            StreamVariableExpander.HasVariables("http://localhost/{not_a_variable}").ShouldBeFalse();
        }

        [Test]
        public void Should_Be_True_For_Channel_Number()
        {
            StreamVariableExpander.HasVariables("http://localhost/{channel_number}").ShouldBeTrue();
        }

        [Test]
        public void Should_Be_True_For_Query_Variable()
        {
            StreamVariableExpander.HasVariables("http://localhost/?r={query:region}").ShouldBeTrue();
        }
    }

    [TestFixture]
    public class ExpandUnescaped
    {
        [Test]
        public void Should_Expand_Channel_Number()
        {
            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?id={channel_number}",
                "30");

            result.ShouldBe("http://localhost:8000/stream?id=30");
        }

        [Test]
        public void Should_Use_Default_When_Channel_Number_Is_Missing()
        {
            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?id={channel_number|0}",
                Option<string>.None);

            result.ShouldBe("http://localhost:8000/stream?id=0");
        }

        [Test]
        public void Should_Expand_To_Empty_When_Channel_Number_Is_Missing_Without_Default()
        {
            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?id={channel_number}",
                Option<string>.None);

            result.ShouldBe("http://localhost:8000/stream?id=");
        }

        [Test]
        public void Should_Expand_Query_Variable()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "midwest" };

            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest");
        }

        [Test]
        public void Should_Match_Query_Variable_Case_Insensitively()
        {
            var parameters = new Dictionary<string, string> { ["Region"] = "midwest" };

            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest");
        }

        [Test]
        public void Should_Use_Default_When_Query_Parameter_Is_Missing()
        {
            var parameters = new Dictionary<string, string>();

            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?r={query:region|default-region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=default-region");
        }

        [Test]
        public void Should_Expand_To_Empty_When_Query_Parameter_Is_Missing_Without_Default()
        {
            var parameters = new Dictionary<string, string>();

            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=");
        }

        [Test]
        public void Should_Expand_Multiple_Variables()
        {
            var parameters = new Dictionary<string, string> { ["lang"] = "en" };

            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?id=etv-{channel_number}-{query:lang}&l={query:lang}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?id=etv-30-en&l=en");
        }

        [Test]
        public void Should_Leave_Unknown_Braced_Content_Unchanged()
        {
            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/{not_a_variable}/stream?id={channel_number}",
                "30");

            result.ShouldBe("http://localhost:8000/{not_a_variable}/stream?id=30");
        }

        [Test]
        public void Should_Treat_Null_Parameters_As_Empty()
        {
            string result = StreamVariableExpander.ExpandUnescaped(
                "http://localhost:8000/stream?r={query:region|fallback}",
                "30",
                null);

            result.ShouldBe("http://localhost:8000/stream?r=fallback");
        }

        [Test]
        public void Should_Expand_Script_Command_Line()
        {
            var parameters = new Dictionary<string, string> { ["profile"] = "hd" };

            string result = StreamVariableExpander.ExpandUnescaped(
                "/usr/local/bin/generate.sh --channel {channel_number} --profile {query:profile|sd}",
                "5",
                parameters);

            result.ShouldBe("/usr/local/bin/generate.sh --channel 5 --profile hd");
        }
    }

    [TestFixture]
    public class ExpandUrl
    {
        [Test]
        public void Should_Leave_Ordinary_Values_Unchanged()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "midwest" };

            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest");
        }

        [Test]
        public void Should_Encode_Value_That_Would_Inject_Another_Parameter()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "midwest&apikey=stolen" };

            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest%26apikey%3Dstolen");
        }

        [Test]
        public void Should_Encode_Value_That_Would_Traverse_Path()
        {
            var parameters = new Dictionary<string, string> { ["path"] = "../../admin" };

            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/{query:path}/live.m3u8",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/..%2F..%2Fadmin/live.m3u8");
            new Uri(result).AbsolutePath.ShouldBe("/..%2F..%2Fadmin/live.m3u8");
        }

        [Test]
        public void Should_Fall_Back_To_Defaults_When_Value_Would_Change_Host()
        {
            var parameters = new Dictionary<string, string> { ["host"] = "evil.example.com" };

            string result = StreamVariableExpander.ExpandUrl(
                "http://{query:host|cdn.example.com}:8000/live.m3u8",
                "30",
                parameters);

            result.ShouldBe("http://cdn.example.com:8000/live.m3u8");
        }

        [Test]
        public void Should_Use_Default_When_Value_Is_Too_Long()
        {
            var parameters = new Dictionary<string, string> { ["region"] = new('a', 257) };

            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?r={query:region|central}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=central");
        }

        [Test]
        public void Should_Use_Default_When_Value_Contains_Control_Characters()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "mid\nwest" };

            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?r={query:region|central}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=central");
        }

        [Test]
        public void Should_Not_Encode_Administrator_Authored_Default()
        {
            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/{query:path|region/west/hd}/live.m3u8",
                "30",
                new Dictionary<string, string>());

            result.ShouldBe("http://localhost:8000/region/west/hd/live.m3u8");
        }

        [Test]
        public void Should_Not_Encode_Channel_Number()
        {
            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?id={channel_number}",
                "30.1",
                new Dictionary<string, string>());

            result.ShouldBe("http://localhost:8000/stream?id=30.1");
        }

        [Test]
        public void Should_Treat_Null_Parameters_As_Empty()
        {
            string result = StreamVariableExpander.ExpandUrl(
                "http://localhost:8000/stream?r={query:region|central}",
                "30",
                null);

            result.ShouldBe("http://localhost:8000/stream?r=central");
        }

        [Test]
        public void Should_Refuse_Caller_Values_When_Template_Has_No_Origin_To_Preserve()
        {
            var parameters = new Dictionary<string, string> { ["name"] = "supplied" };

            // a relative template has no scheme, host or port to hold the
            // substitution to, so caller-supplied values cannot be bounded
            string result = StreamVariableExpander.ExpandUrl(
                "streams/{query:name|default}.m3u8",
                "30",
                parameters);

            result.ShouldBe("streams/default.m3u8");
        }

        [Test]
        public void Should_Resolve_Channel_Number_Without_Any_Parameters()
        {
            // the playout document handed to the next engine is built with no
            // request parameters, but the channel is known; {channel_number}
            // must resolve there exactly as it does during legacy playback
            string result = StreamVariableExpander.ExpandUrl(
                "http://headend.local/feeds/{channel_number}/master.m3u8",
                "101",
                null);

            result.ShouldBe("http://headend.local/feeds/101/master.m3u8");
        }

        [Test]
        public void Should_Compare_Origin_Using_The_Channel_Number()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "west" };

            // the channel number is part of the origin the caller's value has to
            // agree with, so a template that builds its host from the channel is
            // not mistaken for a redirected one
            string result = StreamVariableExpander.ExpandUrl(
                "http://ch{channel_number}.cdn.example/{query:region|central}/live.m3u8",
                "30",
                parameters);

            result.ShouldBe("http://ch30.cdn.example/west/live.m3u8");
        }
    }

    [TestFixture]
    public class ExpandWithDefaults
    {
        [Test]
        public void Should_Use_Defaults_For_All_Variables()
        {
            string result = StreamVariableExpander.ExpandWithDefaults(
                "http://localhost:8000/stream?id={channel_number|1}&r={query:region|central}");

            result.ShouldBe("http://localhost:8000/stream?id=1&r=central");
        }

        [Test]
        public void Should_Expand_To_Empty_Without_Defaults()
        {
            string result = StreamVariableExpander.ExpandWithDefaults(
                "http://localhost:8000/stream?id={channel_number}&r={query:region}");

            result.ShouldBe("http://localhost:8000/stream?id=&r=");
        }

        [Test]
        public void Should_Consume_Every_Variable()
        {
            // an already-expanded url carries no variables, so it cannot be
            // expanded a second time; callers that need the channel number must
            // expand the stored template rather than a resolved path
            string result = StreamVariableExpander.ExpandWithDefaults(
                "http://localhost:8000/stream?id={channel_number|1}&r={query:region|central}");

            StreamVariableExpander.HasVariables(result).ShouldBeFalse();
        }
    }
}
