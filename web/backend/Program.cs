using RoverOperatorApi.Hubs;
using RoverOperatorApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddSingleton<ILatestTelemetryStore, LatestTelemetryStore>();
builder.Services.AddSingleton<IRoverSerialService, RoverSerialService>();
builder.Services.AddSingleton<ILcdService, LcdService>();
builder.Services.AddSingleton<IIrService, IrService>();
builder.Services.AddSingleton<ICameraService, CameraService>();
builder.Services.AddSingleton<IVideoQualityService, VideoQualityService>();
builder.Services.AddSingleton<ICameraStreamService, CameraStreamService>();
builder.Services.AddSingleton<IAudioStreamService, AudioStreamService>();
builder.Services.AddSingleton<ISystemInfoService, SystemInfoService>();
builder.Services.AddSingleton<ILcdAutoUpdateService, LcdAutoUpdateService>();
builder.Services.AddHostedService<LcdAutoUpdateBackgroundService>();
builder.Services.AddHostedService<TelemetryBackgroundService>();
builder.Services.AddHostedService<HeartbeatBackgroundService>();

var app = builder.Build();

app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<DriveHub>("/hubs/drive");
app.MapHub<SoundHub>("/hubs/sound");
app.MapFallbackToFile("index.html");

app.Run();
