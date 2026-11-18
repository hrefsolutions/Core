using Href.Core.Api.Common.Persistence;
using Href.Core.Api.Common.Web;
using Href.Core.Api.Modules.Applications;
using Microsoft.AspNetCore.Authentication;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddControllers();
    builder.Services.AddProblemDetails(options =>
        options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

            if (context.HttpContext.Items[CorrelationIdMiddleware.HeaderName] is string correlationId)
            {
                context.ProblemDetails.Extensions["correlationId"] = correlationId;
            }
        });
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddNHibernate(builder.Configuration);
    builder.Services.AddScoped<ApplicationService>();
    builder.Services
        .AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, AppKeyAuthenticationHandler>(
            AppKeyAuthentication.SchemeName,
            _ => { });
    builder.Services
        .AddAuthorizationBuilder()
        .AddPolicy(AppKeyAuthentication.PolicyName, policy => policy
            .AddAuthenticationSchemes(AppKeyAuthentication.SchemeName)
            .RequireAuthenticatedUser());

    var app = builder.Build();

    DatabaseMigrator.MigrateIfEnabled(app.Configuration, app.Environment, app.Logger);

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Application terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
