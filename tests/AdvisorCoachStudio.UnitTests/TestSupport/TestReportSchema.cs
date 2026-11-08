using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>
/// One report schema shared by the test assembly (building it is comparatively expensive). Tests that need extra
/// instances, such as the multi-instance regression tests in ReportSchemaTests, construct their own.
/// </summary>
internal static class TestReportSchema
{
    private static readonly Lazy<ReportSchema> LazyShared = new(() => new ReportSchema(RepoPaths.ReportSchemaFile, TestScenarios.Catalog));

    public static ReportSchema Shared => LazyShared.Value;
}
