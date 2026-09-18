using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ArrResearchServiceTests
{
    private sealed class FakeArrClient(string[] rootFolders, bool removeAndSearchSucceeds)
        : ArrClient("http://fake", "key")
    {
        public int RemoveAndSearchCalls { get; private set; }

        public override Task<List<ArrRootFolder>> GetRootFolders() =>
            Task.FromResult(rootFolders.Select(x => new ArrRootFolder { Path = x }).ToList());

        public override Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
        {
            RemoveAndSearchCalls++;
            return Task.FromResult(removeAndSearchSucceeds);
        }
    }

    [Fact]
    public async Task TheArrOwningTheLinkIsAskedToRemoveAndSearch()
    {
        var owner = new FakeArrClient(["/media/movies"], removeAndSearchSucceeds: true);

        var accepted = await ArrResearchService.TryRemoveAndSearchAsync(
            [owner], "/media/movies/Some Movie/movie.mkv");

        Assert.True(accepted);
        Assert.Equal(1, owner.RemoveAndSearchCalls);
    }

    [Fact]
    public async Task AnArrWhoseRootFoldersDoNotContainTheLinkIsSkipped()
    {
        var stranger = new FakeArrClient(["/media/tv"], removeAndSearchSucceeds: true);
        var owner = new FakeArrClient(["/media/movies"], removeAndSearchSucceeds: true);

        var accepted = await ArrResearchService.TryRemoveAndSearchAsync(
            [stranger, owner], "/media/movies/Some Movie/movie.mkv");

        Assert.True(accepted);
        Assert.Equal(0, stranger.RemoveAndSearchCalls);
        Assert.Equal(1, owner.RemoveAndSearchCalls);
    }

    [Fact]
    public async Task NoMatchingArrMeansNobodyIsAsked()
    {
        var stranger = new FakeArrClient(["/media/tv"], removeAndSearchSucceeds: true);

        var accepted = await ArrResearchService.TryRemoveAndSearchAsync(
            [stranger], "/media/movies/Some Movie/movie.mkv");

        Assert.False(accepted);
        Assert.Equal(0, stranger.RemoveAndSearchCalls);
    }

    [Fact]
    public async Task LaterArrsAreNotTriedOnceTheOwningArrDeclines()
    {
        // characterizes the existing `break`: if the Arr that owns the root folder
        // has no matching media item, the caller falls back to deleting the link
        // rather than handing the path to an unrelated Arr instance.
        var owner = new FakeArrClient(["/media/movies"], removeAndSearchSucceeds: false);
        var other = new FakeArrClient(["/media/movies"], removeAndSearchSucceeds: true);

        var accepted = await ArrResearchService.TryRemoveAndSearchAsync(
            [owner, other], "/media/movies/Some Movie/movie.mkv");

        Assert.False(accepted);
        Assert.Equal(1, owner.RemoveAndSearchCalls);
        Assert.Equal(0, other.RemoveAndSearchCalls);
    }
}
