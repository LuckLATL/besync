using BeSync.Commands.Merging;
using Spectre.Console.Cli;

// Make merge command default command
var app = new CommandApp<MergeCommand>();
app.Configure(config =>
{
    config.AddCommand<MergeCommand>("merge");
});

// Run Spectre Console
return await app.RunAsync(args);
