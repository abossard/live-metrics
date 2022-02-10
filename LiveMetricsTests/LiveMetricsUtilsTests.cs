using FluentAssertions;
using LiveMetrics;
using Xunit;

namespace LiveMetricsTests;

public class LiveMetricsUtilsTests
{
    [Fact]
    public void TestApplyWildcardToFilterList()
    {
        // arrange
        var haystack = new[]
        {
            "namespace.domain-special-characters.class",
            "namespace.domain-special-characters.class2",
            "namespace.domain-special-characters.class3",
            "start-match",
            "match-tail",
            "full-match",
            "never-find-this"
        };
        var needles = new[]
        {
            "namespace.domain*",
            "no-match",
            "*match",
            "match*",
            "full-match"
        };

        var goal = new[]
        {
            "namespace.domain-special-characters.class",
            "namespace.domain-special-characters.class2",
            "namespace.domain-special-characters.class3",
            "start-match",
            "match-tail",
            "full-match"
        };
        
        // act
        var result = LiveMetricsUtils.ApplyWildcardToFilterList(haystack, needles);
        
        // assert
        result.Should().BeEquivalentTo(goal);
    }
}
