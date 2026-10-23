using TownSuite.WorkQueues.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTopicServer(builder.Configuration);

var app = builder.Build();
app.UseTopicServer();
app.Run();

/// <summary>Entry point; public so integration tests can host the server.</summary>
public partial class Program;
