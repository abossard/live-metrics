using LiveMetrics.LiveMetricsTasks;
using Prometheus;
using Serilog;

// from https://github.com/datalust/dotnet6-serilog-example/blob/dev/Program.cs
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

Log.Information("Starting up");

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, lc) => lc
        .WriteTo.Console()
        .ReadFrom.Configuration(ctx.Configuration));

    builder.Services.AddControllers();

    builder.Services.Configure<StorageAccountMetricsOptions>(
        builder.Configuration.GetSection("LiveMetrics").GetSection(nameof(StorageAccountMetricsOptions))
    );
    builder.Services.AddHostedService<StorageAccountMetricsService>();

    var app = builder.Build();
    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();

    app.UseAuthorization();
    app.UseHttpMetrics();
    app.UseMetricServer();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.Information("Shut down complete");
    Log.CloseAndFlush();
}