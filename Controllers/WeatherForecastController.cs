using Microsoft.AspNetCore.Mvc;
using Prometheus;

namespace webapi.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly Counter CallCounter = Metrics.CreateCounter("call_counter_weather_forecast", "weather forecast");
    private static readonly Counter NewCallCounter = Prometheus.Metrics.CreateCounter("call_counter_new", "new");
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<WeatherForecastController> _logger;

    public WeatherForecastController(ILogger<WeatherForecastController> logger)
    {
        _logger = logger;
    }

    [HttpGet(Name = "GetWeatherForecast")]
    public IEnumerable<WeatherForecast> Get()
    {
        CallCounter.Inc();
        NewCallCounter.Inc();
        _logger.LogInformation("Information, yes {magicNumber} !!!!!", 13);
        _logger.LogWarning("Warning, yes!!!!!");
        _logger.LogError("Error, yes 13 !!!!!");
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = DateTime.Now.AddDays(index),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)]
        })
        .ToArray();
    }
}
