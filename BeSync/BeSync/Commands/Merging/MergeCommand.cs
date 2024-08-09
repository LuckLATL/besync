using System.ComponentModel;
using BeSync.Extensions;
using BeSync.Models;
using BeSync.Models.Console;
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
                        FilePath = targetPath,
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

        string answer = AnsiConsole.Prompt(new TextPrompt<string>("Do you wish to continue?").AddChoices(["y", "n"]).DefaultValue("y"));
        if (answer == "n")
            return 0;

        await AnsiConsole.Status()
            .StartAsync("Preparing file...", async context =>
            {
                var result = await FFMpegArguments
                    .FromFileInput(targetPath)
                    .AddFileInput(inputPath)
                    .OutputToFile(outputPath, overwrite: true, options =>
                    {
                        options.WithVideoCodec("copy"); // Copy the video stream
                        options.WithAudioCodec("aac"); // Encode the audio stream
                        options.WithCustomArgument("-map 0:v:0"); // Map video from the first input

                        int originalAudioTrackCount = selectedAudioTracks.Count(x => originalVideoFile.AudioTracks.Contains(x));
                        string filterComplex = ""; // Initialize filter complex for delay
                        int filterIndex = 0; // Index for filter outputs
                        int metadataIndex = 0; // Index for metadata adjustment

                        foreach (var stream in selectedAudioTracks)
                        {
                            var track = stream as AudioTrack;
                            int audioStreamIndex = -1;

                            if (originalVideoFile.AudioTracks.Contains(stream))
                            {
                                // Map original audio tracks directly
                                audioStreamIndex = originalTrackMediaInfo.AudioStreams.IndexOf(originalTrackMediaInfo.AudioStreams.FirstOrDefault(x => x.Index == track.Index));
                                options.WithCustomArgument($"-map 0:a:{audioStreamIndex}");
                            }
                            else
                            {
                                // Map non-original tracks and add delay
                                audioStreamIndex = additionalTrackMediaInfo.AudioStreams.IndexOf(additionalTrackMediaInfo.AudioStreams.FirstOrDefault(x => x.Index == track.Index));
                                // Apply delay to non-original audio tracks
                                if (!string.IsNullOrEmpty(filterComplex))
                                {
                                    filterComplex += ";";
                                }

                                if (averageOffset > 0)
                                    filterComplex += $"[1:a:{audioStreamIndex}]adelay={averageOffset}|{averageOffset}[a{filterIndex}]"; // Add delay filter for each non-original track
                                else if (averageOffset < 0)
                                    filterComplex += $"[1:a:{audioStreamIndex}]atrim=start={Math.Abs(averageOffset / 1000)},asetpts=PTS-STARTPTS[a{filterIndex}]";
                                options.WithCustomArgument($"-map [a{filterIndex}]"); // Map the delayed track
                                filterIndex++;
                                metadataIndex++;
                            }

                            // Set metadata for all tracks
                            options.WithCustomArgument($"-metadata:s:a:{audioStreamIndex + originalAudioTrackCount} language={track.Language.LanguageCodeLong}");
                            options.WithCustomArgument($"-metadata:s:a:{audioStreamIndex + originalAudioTrackCount} title=\"{track.Title}\"");
                        }

                        // Apply the filter complex if there are any delays to apply
                        if (!string.IsNullOrEmpty(filterComplex))
                        {
                            options.WithCustomArgument($"-filter_complex \"{filterComplex}\"");
                        }

                        // Ensure the output duration matches the shortest input
                        options.WithCustomArgument("-shortest");
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

    private async Task<int> PerformAutoMatching(string mainVideoPath, string videoToMatchPath)
    {
        // Get metadata for the input video with audio
        var additionalTrackMediaInfo = FFProbe.Analyse(videoToMatchPath);
        var originalTrackMediaInfo = FFProbe.Analyse(mainVideoPath);

        int numberOfProbes = 5;
        double similarityThreshold = 95;
        int samplesPerSecond = 10;
        int searchAreaSeconds = 10;

        List<(double similarity, int offset)> analysedFrames = new();

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new TaskCountColumn()
            })
            .StartAsync(async ctx =>
            {
                // Define tasks
                var matchingTask = ctx.AddTask("[green]Probes[/]", true, numberOfProbes);
                var searchingTask = ctx.AddTask("[gray]Frames[/]", true, 2 * searchAreaSeconds * samplesPerSecond);


                for (int i = 0; i < numberOfProbes; i++)
                {
                    // Randomly choose a frame within the video duration minus 2 minutes to ensure range
                    double randomFrameTime = (new Random().NextDouble() * (originalTrackMediaInfo.Duration.TotalMilliseconds - searchAreaSeconds * 1000 * 2)) + searchAreaSeconds * 1000;
                    TimeSpan frameTime = TimeSpan.FromMilliseconds(randomFrameTime);

                    ulong mainImageHash;

                    try
                    {
                        using (var bitmapStream = new MemoryStream())
                        {
                            // Use FFmpeg to extract a single frame
                            var result = FFMpegArguments
                                .FromFileInput(mainVideoPath, true, options => options
                                    .Seek(frameTime)) // Seek to the specific frame time
                                .OutputToPipe(new StreamPipeSink(bitmapStream), options => options
                                    .WithVideoCodec("bmp") // Use BMP codec to output the frame
                                    .ForceFormat("image2") // Force image format
                                    .WithFrameOutputCount(1)) // Output only one frame
                                .ProcessSynchronously();

                            bitmapStream.Position = 0; // Reset stream position for reading

                        var avgHash = new AverageHash();
                        mainImageHash = avgHash.Hash(bitmapStream);
                        bitmapStream.Position = 0;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error extracting frame: {ex.Message}[/]");
                        continue;
                    }

                    analysedFrames.AddRange(SearchFrame(samplesPerSecond, searchAreaSeconds, searchingTask, mainImageHash, videoToMatchPath, frameTime, SearchDirection.Before));
                    analysedFrames.AddRange(SearchFrame(samplesPerSecond, searchAreaSeconds, searchingTask, mainImageHash, videoToMatchPath, frameTime, SearchDirection.After));

                    searchingTask.Value = 0;
                    matchingTask.Value++;
                }
            });

        var matchedFrames = analysedFrames.OrderByDescending(x => x.similarity).Take(10).ToList();
        
        int offset = Convert.ToInt32(matchedFrames.Select(x => x.offset).Average());
        AnsiConsole.MarkupLine($"Out of [yellow]{numberOfProbes}[/] probes, [green]{analysedFrames.Count}[/] offsets were recorded with a median of [purple]{offset} milliseconds[/] ({string.Join(", ", matchedFrames.Select(x => x.offset))}).");
        
        return offset;
    }

    private List<(double similarity, int offset)> SearchFrame(int samplesPerSecond, int searchAreaSeconds, ProgressTask frameSearchTask, ulong targetFrameHash, string videoToMatchPath, TimeSpan startTime, SearchDirection direction)
    {
        List<(double similarity, int offset)> searchedFrames = new();

        ulong compareFrameHash = 0;
        TimeSpan currentProbeFrame = startTime;
        int offset = 0;
        bool matchFound = false;

        for (int i = 0; i < samplesPerSecond * searchAreaSeconds; i++)
        {
            currentProbeFrame = startTime.Add(TimeSpan.FromMilliseconds(offset));

            try
            {
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
                    //File.WriteAllBytes("/home/void/Desktop/videotest/image2.bmp", ReadFully(searchBitmapStream));
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error extracting frame: {ex.Message}[/]");
                continue;
            }

            double similarity = CompareHash.Similarity(targetFrameHash, compareFrameHash);
            searchedFrames.Add(new(similarity, offset));

            if (similarity > 95)
                AnsiConsole.MarkupLine($"[gray]{offset}[/] {similarity}");

            frameSearchTask.Value++;

            if (direction == SearchDirection.Before)
                offset -= (1000 / samplesPerSecond);
            else
                offset += (1000 / samplesPerSecond);
        }

        searchedFrames = searchedFrames.GroupBy(item => item.similarity)
            .Select(group => (
                similarity: group.Key,
                offset: Convert.ToInt32(group.Average(item => item.offset))
            ))
            .ToList();

        return searchedFrames;
    }


    private enum SearchDirection
    {
        Before,
        After
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