using APIArena.Infrastructure;
using APIArena.Worker;
using APIArena.Infrastructure.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddDeploymentFile();

builder.Services.AddApiArenaCore(builder.Configuration, builder.Environment);
builder.Services.AddWorkerJobs(builder.Configuration);

var host = builder.Build();
host.Run();
