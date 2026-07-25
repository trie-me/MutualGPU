using MutualGPU.Api;
using MutualGPU.Domain;

namespace MutualGPU.Api.Tests;

public sealed class TaskUpdateHubTests
{
    [Fact]
    public async Task Publishes_only_to_the_matching_requestor_and_coalesces_a_burst()
    {
        var hub = new TaskUpdateHub();
        var requestor = RequestorId.New();
        var otherRequestor = RequestorId.New();
        await using var subscriber = hub.Subscribe(requestor);
        await using var otherSubscriber = hub.Subscribe(otherRequestor);

        hub.Publish(requestor);
        hub.Publish(requestor);

        Assert.True(await subscriber.Reader.WaitToReadAsync(CancellationToken.None));
        Assert.True(subscriber.Reader.TryRead(out _));
        Assert.False(subscriber.Reader.TryRead(out _));
        Assert.False(otherSubscriber.Reader.TryRead(out _));
    }
}
