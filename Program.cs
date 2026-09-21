using AudioStreaming.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSignalR().AddMessagePackProtocol();
builder.Services.AddSingleton<AudioStreamPublisher>();
var browserOrigins = builder.Configuration.GetSection("BrowserOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (browserOrigins.Length > 0)
        policy.WithOrigins(browserOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
builder.Services.AddOptions<AudioCaptureOptions>()
    .Bind(builder.Configuration.GetSection("AudioCapture"))
    .Validate(o => double.IsFinite(o.StartThresholdDbfs) && o.StartThresholdDbfs >= -120 && o.StartThresholdDbfs < 0,
        "AudioCapture:StartThresholdDbfs must be between -120 and 0 (exclusive of 0).")
    .Validate(o => o.StartConfirmationMs >= 0 && o.StartConfirmationMs <= 1000,
        "AudioCapture:StartConfirmationMs must be between 0 and 1000.")
    .Validate(o => o.SilenceTimeoutSeconds >= 1 && o.SilenceTimeoutSeconds <= 3600,
        "AudioCapture:SilenceTimeoutSeconds must be between 1 and 3600.")
    .ValidateOnStart();
builder.Services.AddSingleton<OpusChunkEncoder>();
builder.Services.AddSingleton<IAudioChunkProcessor, AudioChunkProcessor>();
builder.Services.AddSingleton<AudioCaptureService>();
builder.Services.AddSingleton<IAutomatedCapture>(provider => provider.GetRequiredService<AudioCaptureService>());
builder.Services.Configure<PlaybackAutomationOptions>(builder.Configuration.GetSection("PlaybackAutomation"));
builder.Services.AddHttpClient("PlaybackControl").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<HttpPlaybackControl>();
builder.Services.AddSingleton<VlcPlaybackControl>();
builder.Services.AddSingleton<MediaInputStore>();
builder.Services.Configure<AudioMediaOptions>(builder.Configuration.GetSection("AudioMedia"));
builder.Services.AddSingleton<IPlaybackControl, PlaybackControlRouter>();
builder.Services.AddSingleton<PlaybackAutomationService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AudioCaptureService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<PlaybackAutomationService>());
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors();

app.UseAuthorization();

app.MapControllers();
app.MapHub<AudioStreaming.Backend.Hubs.AudioHub>("/hubs/audio");

app.Run();
