var builder = DistributedApplication.CreateBuilder(args);

// Two demos share this AppHost. The default is the original single-server MRTR demo. The "scaleout" launch
// profile (or --Demo scaleout) runs two server instances behind a round-robin gateway instead.
if (string.Equals(builder.Configuration["Demo"], "scaleout", StringComparison.OrdinalIgnoreCase))
{
    ScaleOutDemo.Add(builder);
}
else
{
    MrtrDemo.Add(builder);
}

builder.Build().Run();
