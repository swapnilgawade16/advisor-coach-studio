using AdvisorCoachStudio.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAdvisorCoachStudio(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseAdvisorCoachStudio();
app.Run();

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
