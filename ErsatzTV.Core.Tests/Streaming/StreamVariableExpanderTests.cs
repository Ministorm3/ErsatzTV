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
    public class Expand
    {
        [Test]
        public void Should_Expand_Channel_Number()
        {
            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?id={channel_number}",
                "30");

            result.ShouldBe("http://localhost:8000/stream?id=30");
        }

        [Test]
        public void Should_Use_Default_When_Channel_Number_Is_Missing()
        {
            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?id={channel_number|0}",
                Option<string>.None);

            result.ShouldBe("http://localhost:8000/stream?id=0");
        }

        [Test]
        public void Should_Expand_To_Empty_When_Channel_Number_Is_Missing_Without_Default()
        {
            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?id={channel_number}",
                Option<string>.None);

            result.ShouldBe("http://localhost:8000/stream?id=");
        }

        [Test]
        public void Should_Expand_Query_Variable()
        {
            var parameters = new Dictionary<string, string> { ["region"] = "midwest" };

            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest");
        }

        [Test]
        public void Should_Match_Query_Variable_Case_Insensitively()
        {
            var parameters = new Dictionary<string, string> { ["Region"] = "midwest" };

            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=midwest");
        }

        [Test]
        public void Should_Use_Default_When_Query_Parameter_Is_Missing()
        {
            var parameters = new Dictionary<string, string>();

            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?r={query:region|default-region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=default-region");
        }

        [Test]
        public void Should_Expand_To_Empty_When_Query_Parameter_Is_Missing_Without_Default()
        {
            var parameters = new Dictionary<string, string>();

            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?r={query:region}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?r=");
        }

        [Test]
        public void Should_Expand_Multiple_Variables()
        {
            var parameters = new Dictionary<string, string> { ["lang"] = "en" };

            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?id=etv-{channel_number}-{query:lang}&l={query:lang}",
                "30",
                parameters);

            result.ShouldBe("http://localhost:8000/stream?id=etv-30-en&l=en");
        }

        [Test]
        public void Should_Leave_Unknown_Braced_Content_Unchanged()
        {
            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/{not_a_variable}/stream?id={channel_number}",
                "30");

            result.ShouldBe("http://localhost:8000/{not_a_variable}/stream?id=30");
        }

        [Test]
        public void Should_Treat_Null_Parameters_As_Empty()
        {
            string result = StreamVariableExpander.Expand(
                "http://localhost:8000/stream?r={query:region|fallback}",
                "30",
                null);

            result.ShouldBe("http://localhost:8000/stream?r=fallback");
        }

        [Test]
        public void Should_Expand_Script_Command_Line()
        {
            var parameters = new Dictionary<string, string> { ["profile"] = "hd" };

            string result = StreamVariableExpander.Expand(
                "/usr/local/bin/generate.sh --channel {channel_number} --profile {query:profile|sd}",
                "5",
                parameters);

            result.ShouldBe("/usr/local/bin/generate.sh --channel 5 --profile hd");
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
    }
}
