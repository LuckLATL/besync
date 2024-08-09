using System.ComponentModel;
using BeSync.Extensions;
using BeSync.Models;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using FFMpegCore;
using FFMpegCore.Pipes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BeSync.Commands.Merging;

public class MergeCommand : AsyncCommand<MergeCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-a| --audio-file")]
        [Description("Audio or video file used for input")]
        public string? InputPath { get; init; }

        [CommandOption("-v| --video-file")]
        [Description("File used for as video")]
        public string? TargetPath { get; init; }

        [CommandOption("-o| --output")]
        [Description("File used to output the video")]
        public string? OutputPath { get; init; } 

        [CommandOption("-m| --auto-matching")]
        [Description("Use auto matching to determine the offset of the audio")]
        public bool? AutoMatch { get; init; } = true;
        
        [CommandOption("-i| --interactive")]
        [Description("Show prompts")]
        public bool? IsInteractive { get; init; } = true;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var inputPath = ConfirmFile(settings.InputPath);
        var targetPath = ConfirmFile(settings.TargetPath);
        var outputPath = ConfirmFile(settings.OutputPath);

        // Get metadata for the input video with audio
        IMediaAnalysis? additionalTrackMediaInfo = null;
        IMediaAnalysis? originalTrackMediaInfo = null;

        VideoFile? originalVideoFile = null;
        VideoFile additionalVideoFile = null;

        await AnsiConsole.Status()
            .StartAsync("Analysing files", async context =>
            {
                additionalTrackMediaInfo = await FFProbe.AnalyseAsync(inputPath);
                originalTrackMediaInfo = await FFProbe.AnalyseAsync(targetPath);

                additionalVideoFile = new VideoFile()
                {
                    FilePath = inputPath,
                    AudioTracks = additionalTrackMediaInfo.AudioStreams.Select(x => new AudioTrack()
                    {
                        FilePath = inputPath,
                        Index = x.Index,
                        Language = Language.LookupCode(x.Language),
                    }).ToList()
                };
                
                originalVideoFile = new VideoFile()
                {
                    FilePath = targetPath,
                    AudioTracks = originalTrackMediaInfo.AudioStreams.Select(x => new AudioTrack()
                    {
                        FilePath = inputPath,
                        Index = x.Index,
                        Language = Language.LookupCode(x.Language),
                    }).ToList()
                };
            });

        AnsiConsole.Clear();

        if (additionalTrackMediaInfo == null || originalTrackMediaInfo == null)
            return 0;

        AnsiConsole.Write(new Rule($"Found Audio Tracks - Overview").Justify(Justify.Left));

        var prompt = new MultiSelectionPrompt<ISelectionNode>
        {
            Converter = n => n.GetReadableName(),
            PageSize = 10,
            Title = "Which audio tracks to add to the [yellow]output[/] video?",
            Mode = SelectionMode.Leaf
        }.AddChoiceGroup(originalVideoFile, originalVideoFile.AudioTracks)
        .AddChoiceGroup(additionalVideoFile, additionalVideoFile.AudioTracks).Select(originalVideoFile);
        originalVideoFile.AudioTracks.ForEach(x => prompt.Select(x));
            
        var selectedAudioTracks = AnsiConsole.Prompt(prompt);
        // Display audio track information and check language tags
        foreach (var audioStream in selectedAudioTracks.Where(x => !originalVideoFile.AudioTracks.Contains(x)))
        {
            AnsiConsole.Clear();
            var track = audioStream as AudioTrack;
            
            AnsiConsole.Write(new Rule($"{track.GetReadableName()} @ {Markup.Escape(Path.GetFileName(track.FilePath))}").Justify(Justify.Left));
            var language = AnsiConsole.Prompt(new SelectionPrompt<Language>()
                .Title("What is the [green]language[/] of this audio track?")
                .MoreChoicesText("[grey](Use the arrow keys to move up and down)")
                .AddChoices(Language.AvailableLanguages));
            string title = AnsiConsole.Prompt(new TextPrompt<string>("What is the [green]title[/] of this audio track? "));

            track.Language = language;
            track.Title = title;
        }
        
        AnsiConsole.Clear();

        int averageOffset = await PerformAutoMatching(targetPath, inputPath);
        averageOffset = 1000;
        
        string answer = AnsiConsole.Prompt(new TextPrompt<string>("Do you wish to continue?").AddChoices(["y", "n"]).DefaultValue("y"));
        if (answer == "n")
            return 0;
        
        await AnsiConsole.Status()
            .StartAsync("Preparing file...", async context  => 
            {
                var result = await FFMpegArguments
                    .FromFileInput(targetPath)
                    .AddFileInput(inputPath)
                    .OutputToFile(outputPath, overwrite: true, options =>
                    {
                        options.WithVideoCodec("copy"); // Copy the video stream
                        options.WithAudioCodec("aac"); // Encode the audio stream
                        options.WithCustomArgument("-map 0:v:0"); // Map video from the first input
                        string channelOffsets = string.Empty;
                        foreach (var stream in selectedAudioTracks.Where(x => originalVideoFile.AudioTracks.Contains(x)))
                        {
                            var track = stream as AudioTrack;
                            int audioStreamIndex = additionalTrackMediaInfo.AudioStreams.IndexOf(additionalTrackMediaInfo.AudioStreams.FirstOrDefault(x => x.Index == track.Index));
                            options.WithCustomArgument($"-map 0:a:{audioStreamIndex}"); // Map audio from the first input if it exists

                            if (channelOffsets != string.Empty)
                                channelOffsets += "|";
                            channelOffsets += "0";
                        }
                        
                        foreach (var stream in selectedAudioTracks.Where(x => !originalVideoFile.AudioTracks.Contains(x)))
                        {
                            var track = stream as AudioTrack;
                            int audioStreamIndex = additionalTrackMediaInfo.AudioStreams.IndexOf(additionalTrackMediaInfo.AudioStreams.FirstOrDefault(x => x.Index == track.Index));
                            int originalAudioTrackCount = selectedAudioTracks.Count(x => originalVideoFile.AudioTracks.Contains(x));
                            options.WithCustomArgument($"-map 1:a:{audioStreamIndex}"); // Map selected audio track
                            options.WithCustomArgument($"-metadata:s:{audioStreamIndex+1 + originalAudioTrackCount} language={track.Language.LanguageCodeLong}"); // Set the language metadata
                            options.WithCustomArgument($"-metadata:s:{audioStreamIndex+1 + originalAudioTrackCount} title=\"{track.Title}\""); // Set the language metadata
                            
                            if (channelOffsets != string.Empty)
                                channelOffsets += "|";
                            channelOffsets += averageOffset;
                        }

                        // options.WithCustomArgument("-map 1:a:0"); // Map audio from the second input
                        // options.WithCustomArgument("-ar 44100");  // Resample both audio tracks to 44.1 kHz
                        // options.WithCustomArgument("-shortest"); // Ensure the output duration matches the shortest input
                    })
                    .ProcessAsynchronously();
                
                if (result)
                    AnsiConsole.MarkupLine($"[green]File written to {settings.OutputPath}[/]");
                else
                    AnsiConsole.MarkupLine($"[red]Processing failed. Try again.[/]");
                
            });

        AnsiConsole.Console.Input.ReadKey(false);
        
        return 0;
    }
    
    private static async Task<int> PerformAutoMatching(string mainVideoPath, string videoToMatchPath)
    {
        // Get metadata for the input video with audio
        var additionalTrackMediaInfo = FFProbe.Analyse(videoToMatchPath);
        var originalTrackMediaInfo = FFProbe.Analyse(mainVideoPath);

        int numberOfProbes = 3;
        double similarityThreshold = 95;
        int sampleRate = 20;

        List<(double, double)> recordedOffsets = new List<(double, double)>();
        
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                // Define tasks
                var matchingTask = ctx.AddTask("[green]Matched Frames[/]", true, numberOfProbes);
                var searchingTask = ctx.AddTask("[gray]Searched Frames[/]", true, 1200);
                
                for (int i = 0; i < numberOfProbes; i++)
                {
                    double frameToProbe = new Random().NextInt64(Convert.ToInt64(originalTrackMediaInfo.Duration.TotalMilliseconds));
                    TimeSpan frameTime = TimeSpan.FromMilliseconds(frameToProbe);

                    ulong mainImageHash;
                    using (var bitmapStream = new MemoryStream())
                    {
                        // Use FFmpeg to extract a single frame
                        var result = FFMpegArguments
                            .FromFileInput(mainVideoPath, true, options => options
                                .Seek(frameTime)) // Seek to the specific frame time
                            .OutputToPipe(new StreamPipeSink(bitmapStream), options => options
                                .WithVideoCodec("bmp") // Use BMP codec to output the frame
                                .ForceFormat("image2")   // Force image format
                                .WithFrameOutputCount(1)) // Output only one frame
                            .ProcessSynchronously();

                        bitmapStream.Position = 0; // Reset stream position for reading

                        var avgHash = new AverageHash();
                        mainImageHash = avgHash.Hash(bitmapStream);
                        bitmapStream.Position = 0;
                    }

                    ulong compareFrameHash = 0;
                    TimeSpan currentProbeFrame = frameTime;
                    int offset = 0;
                    bool matchBoundsReached = false;
                    OffsetDirection direction = OffsetDirection.Negative;
                    do
                    {
                        currentProbeFrame = frameTime.Add(TimeSpan.FromMilliseconds(offset));
                        
                        using (var searchBitmapStream = new MemoryStream())
                        {
                            // Use FFmpeg to extract a single frame
                            var result = FFMpegArguments
                                .FromFileInput(videoToMatchPath, true, options => options
                                    .Seek(currentProbeFrame)) // Seek to the specific frame time
                                .OutputToPipe(new StreamPipeSink(searchBitmapStream), options => options
                                    .WithVideoCodec("bmp") // Use BMP codec to output the frame
                                    .ForceFormat("image2") // Force image format
                                    .WithFrameOutputCount(1)) // Output only one frame
                                .ProcessSynchronously();

                            searchBitmapStream.Position = 0; // Reset stream position for reading

                            var avgHash = new AverageHash();
                            compareFrameHash = avgHash.Hash(searchBitmapStream);
                            searchBitmapStream.Position = 0;
                        }

                        if (direction == OffsetDirection.Negative)
                        {
                            if (offset > 0)
                                offset = 0;
                            offset -= (1000 / sampleRate);

                            if (offset < - (1000 / sampleRate) * 600)
                                direction = OffsetDirection.Positive;
                        }
                        else if (direction == OffsetDirection.Positive)
                        {
                            if (offset < 0)
                                offset = 0;
                            offset += (1000 / sampleRate);

                            if (offset > (1000 / sampleRate) * 600)
                                matchBoundsReached = true;
                        }
                        
                        searchingTask.Value++;
                    } while (matchBoundsReached);

                    searchingTask.Value = 0;
                    
                    if (!matchBoundsReached)
                        recordedOffsets.Add((frameTime.Subtract(currentProbeFrame).TotalMilliseconds, 0));
                    
                    matchingTask.Value++;
                }
            });
        
        AnsiConsole.MarkupLine($"Out of [yellow]{numberOfProbes}[/] probes, [green]{recordedOffsets.Count}[/] offsets where recorded with an average of [purple]{recordedOffsets.Select(x => x.Item1).Average()} milliseconds[/] ({string.Join(", ", recordedOffsets)}).");
        
        return 0;
    }
    
    private enum OffsetDirection
    {
        Positive,
        Negative
    }
    
    public static byte[] ReadFully(Stream input)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            input.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static string ConfirmFile(string? current)
    {
        return !string.IsNullOrWhiteSpace(current)
            ? current
            : AnsiConsole.Prompt(new TextPrompt<string>("Path > ").Validate(path =>
            {
                if (!File.Exists(path))
                    return ValidationResult.Error("[red]Directory not found. Please try again.[/]");
                return ValidationResult.Success();
            }));
    }
}