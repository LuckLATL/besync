namespace BeSync.VideoMatching;

public interface IVideoMatcher
{
    public Task<int> PerformAutoMatchingAsync(string mainVideoPath, string videoToMatchPath);
}