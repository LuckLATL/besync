using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using FFMpegCore;
using FFMpegCore.Pipes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeSync.VideoMatching
{
    public class AdvancedVideoMatcher : IVideoMatcher
    {
        private const int FrameSampleRate = 10; // Sample every 10th frame
        private const int HashMatchThreshold = 10; // Hamming distance threshold for matching
        private readonly AverageHash _hashAlgorithm = new AverageHash();

        public async Task<int> PerformAutoMatchingAsync(string targetPath, string inputPath)
        {
            // Extract frames and calculate hashes for both videos
            var targetFrameHashes = await ExtractAndHashFramesAsync(targetPath);
            var inputFrameHashes = await ExtractAndHashFramesAsync(inputPath);

            // Calculate the offset using frame hash comparison
            int offset = CalculateOffset(targetFrameHashes, inputFrameHashes);

            return offset;
        }

        private async Task<ConcurrentDictionary<int, ulong>> ExtractAndHashFramesAsync(string videoPath)
        {
            var frameHashes = new ConcurrentDictionary<int, ulong>();

            await FFMpegArguments
                .FromFileInput(videoPath)
                .OutputToPipe(new RawVideoPipeSink(frame =>
                {
                    Console.WriteLine($"Received frame {frame.Index} ({frame.Width}x{frame.Height})");
                    // Only process every Nth frame
                    if (frame.Index % FrameSampleRate == 0)
                    {
                        // Convert raw frame data to an ImageSharp image for hashing
                        using var image = Image.LoadPixelData<Rgba32>(frame.Data, frame.Width, frame.Height);
                        var hash = _hashAlgorithm.Hash(image);
                        frameHashes.TryAdd(frame.Index / FrameSampleRate, hash);
                    }
                }), options => options
                    .ForceFormat("rawvideo")
                    .WithVideoCodec("rawvideo")
                    .WithCustomArgument("-pix_fmt rgba") // Ensure pixel format is RGBA for compatibility
                    .WithCustomArgument($"-vf \"select=not(mod(n\\,{FrameSampleRate})),trim=end_frame=1\"") // Properly escape shell commands
                    .WithCustomArgument("-f h264") // Specify format explicitly
                    .WithCustomArgument("-loglevel debug")) // Output debug info
                .ProcessAsynchronously();

            return frameHashes;
        }

        private int CalculateOffset(ConcurrentDictionary<int, ulong> targetHashes, ConcurrentDictionary<int, ulong> inputHashes)
        {
            int bestOffset = 0;
            int bestMatchCount = 0;

            for (int offset = -targetHashes.Count; offset < targetHashes.Count; offset++)
            {
                int matchCount = 0;

                foreach (var (frameIndex, inputHash) in inputHashes)
                {
                    if (targetHashes.TryGetValue(frameIndex + offset, out var targetHash))
                    {
                        double distance = CompareHash.Similarity(inputHash, targetHash);
                        if (distance <= HashMatchThreshold)
                        {
                            matchCount++;
                        }
                    }
                }

                if (matchCount > bestMatchCount)
                {
                    bestMatchCount = matchCount;
                    bestOffset = offset;
                }
            }

            return bestOffset;
        }
    }

    public class RawVideoPipeSink : IPipeSink
    {
        private readonly Action<FrameData> _processFrame;

        public RawVideoPipeSink(Action<FrameData> processFrame)
        {
            _processFrame = processFrame;
        }

        public Task ReadAsync(Stream inputStream, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public string GetFormat()
        {
            return "rawvideo";
        }

        public async Task WriteAsync(Stream stream, CancellationToken token)
        {
            // Buffer size must accommodate frame resolution and pixel format
            var buffer = new byte[1920 * 1080 * 4]; // 4 bytes per pixel for RGBA
            int frameIndex = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead <= 0) break;

                    var frameData = new FrameData
                    {
                        Index = frameIndex++,
                        Data = new byte[bytesRead],
                        Width = 1920,
                        Height = 1080
                    };

                    Array.Copy(buffer, frameData.Data, bytesRead);
                    _processFrame(frameData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in WriteAsync: {ex.Message}");
            }
        }
    }

    public class FrameData
    {
        public int Index { get; set; }
        public byte[] Data { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
