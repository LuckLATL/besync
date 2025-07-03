using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BeSync.Extensions;
using BeSync.Models;
using BeSync.Models.Console;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using FFmpeg.AutoGen;
using FFMpegCore;
using FFMpegCore.Pipes;
using OpenCvSharp;
using Spectre.Console;

namespace BeSync.VideoMatching;

public class LegacyVideoMatcher : IVideoMatcher
{
    public async Task<int> PerformAutoMatchingAsync(string mainVideoPath, string videoToMatchPath)
    {
        // Get metadata for the input video with audio
        var additionalTrackMediaInfo = FFProbe.Analyse(videoToMatchPath);
        var originalTrackMediaInfo = FFProbe.Analyse(mainVideoPath);

        int numberOfProbes = 40;
        double similarityThreshold = 95;
        int samplesPerSecond = 10;
        int searchAreaSeconds = 5;

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
                            //File.WriteAllBytes("/home/void/Desktop/videotest/image1.bmp", ReadFully(bitmapStream));
                        }
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


        var matchedFrames = analysedFrames.OrderByDescending(x => x.similarity).ToList();
        matchedFrames = matchedFrames.Take(10).ToList();

        int positiveCount = matchedFrames.Count(n => n.offset > 0);
        int negativeCount = matchedFrames.Count(n => n.offset < 0);
        bool keepPositives = positiveCount >= negativeCount;

        matchedFrames = matchedFrames.Where(n => (keepPositives && n.offset > 0) || (!keepPositives && n.offset < 0)).ToList();

        int offset = Convert.ToInt32(matchedFrames.Select(x => x.offset).Median());
        AnsiConsole.MarkupLine($"Out of [yellow]{numberOfProbes}[/] probes, [green]{analysedFrames.Count}[/] offsets were recorded with a median of [purple]{offset} milliseconds[/] ({string.Join(", ", matchedFrames.Select(x => x.offset))}).");

        return -offset;
    }

    private List<(double similarity, int offset)> SearchFrame(int samplesPerSecond, int searchAreaSeconds, ProgressTask frameSearchTask, ulong targetFrameHash, string videoToMatchPath, TimeSpan startTime, SearchDirection direction)
    {
        ConcurrentBag<(double similarity, int offset)> searchedFrames = new();

        ulong compareFrameHash = 0;
        TimeSpan currentProbeFrame = startTime;

        bool matchFound = false;

        Parallel.For(0, samplesPerSecond * searchAreaSeconds, i =>
        {
            int offset = 0;
            if (direction == SearchDirection.Before)
                offset = -(i * (1000 / samplesPerSecond));
            else
                offset = (i * (1000 / samplesPerSecond));

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
                return;
            }

            double similarity = CompareHash.Similarity(targetFrameHash, compareFrameHash);
            searchedFrames.Add(new(similarity, offset));

            if (similarity > 95)
                AnsiConsole.MarkupLine($"[gray]{offset}[/] {similarity}");

            frameSearchTask.Value++;
        });


        List<(double similarity, int offset)> processedFrames = new();
        processedFrames = searchedFrames.GroupBy(item => item.similarity)
            .Select(group => (
                similarity: group.Key,
                offset: Convert.ToInt32(group.Average(item => item.offset))
            ))
            .ToList();

        return processedFrames;
    }
}

