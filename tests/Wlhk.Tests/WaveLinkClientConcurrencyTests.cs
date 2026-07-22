using System.Text.Json.Nodes;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

public sealed class WaveLinkClientConcurrencyTests
{
    [Fact]
    public void ChannelPatchPublishesCopyWithoutMutatingOlderSnapshot()
    {
        using var client = CreateClient();
        LoadChannel(client);
        var olderChannel = Assert.Single(client.GetChannels());
        var olderMix = Assert.Single(olderChannel.Mixes);

        PatchChannel(client, """
            {"id":"mic","name":"Renamed","level":0.6,"isMuted":true,
             "mixes":[
               {"id":"vc","level":0.7,"isMuted":true},
               {"id":"stream","level":0.9,"isMuted":false}
             ]}
            """);

        Assert.Equal(("Microphone", 0.5, false),
            (olderChannel.Name, olderChannel.Level, olderChannel.IsMuted));
        Assert.Equal(("vc", 0.25, false),
            (olderMix.Id, olderMix.Level, olderMix.IsMuted));
        Assert.Single(olderChannel.Mixes);

        var current = Assert.Single(client.GetChannels());
        Assert.NotSame(olderChannel, current);
        Assert.Equal(("Renamed", 0.6, true),
            (current.Name, current.Level, current.IsMuted));
        Assert.Equal(2, current.Mixes.Count);
        Assert.Equal((0.7, true), (current.Mixes[0].Level, current.Mixes[0].IsMuted));
    }

    [Fact]
    public void NamedMixPatchPublishesCopyWithoutMutatingOlderSnapshot()
    {
        using var client = CreateClient();
        client.HandleMessage("""
            {"method":"mixesChanged","params":{"mixes":[
              {"id":"vc","name":"Voice Chat","level":0.5,"isMuted":false}
            ]}}
            """);
        var olderMix = Assert.Single(client.GetMixes());

        client.HandleMessage("""
            {"method":"mixChanged","params":
              {"id":"vc","name":"Renamed","level":0.8,"isMuted":true}}
            """);

        Assert.Equal(("Voice Chat", 0.5, false),
            (olderMix.Name, olderMix.Level, olderMix.IsMuted));
        var current = Assert.Single(client.GetMixes());
        Assert.NotSame(olderMix, current);
        Assert.Equal(("Renamed", 0.8, true),
            (current.Name, current.Level, current.IsMuted));
    }

    [Fact]
    public async Task OptimisticWritesPublishCopiesWithoutMutatingOlderSnapshots()
    {
        using var client = CreateClient();
        LoadChannel(client);
        var beforeChannelWrite = Assert.Single(client.GetChannels());

        client.SetChannel("mic", level: 0.6, isMuted: true);

        Assert.Equal((0.5, false), (beforeChannelWrite.Level, beforeChannelWrite.IsMuted));
        var beforePairWrite = Assert.Single(client.GetChannels());
        Assert.NotSame(beforeChannelWrite, beforePairWrite);
        Assert.Equal((0.6, true), (beforePairWrite.Level, beforePairWrite.IsMuted));

        client.SetChannelMix("mic", "vc", level: 0.7, isMuted: true);

        var olderMix = Assert.Single(beforePairWrite.Mixes);
        Assert.Equal((0.25, false), (olderMix.Level, olderMix.IsMuted));
        var currentMix = Assert.Single(Assert.Single(client.GetChannels()).Mixes);
        Assert.NotSame(olderMix, currentMix);
        Assert.Equal((0.7, true), (currentMix.Level, currentMix.IsMuted));
        await client.WaitForMutationQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MutationDispatchIsFifoWhileSettersRemainNonblocking()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentLevels = new List<double>();
        using var client = new WaveLinkClient(async (_, payload) =>
        {
            double level;
            int position;
            lock (sentLevels)
            {
                level = payload["mixes"]![0]!["level"]!.GetValue<double>();
                sentLevels.Add(level);
                position = sentLevels.Count;
            }
            if (position == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
        });
        LoadChannel(client);

        client.SetChannelMix("mic", "vc", level: 0.1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.SetChannelMix("mic", "vc", level: 0.2);

        lock (sentLevels)
            Assert.Equal([0.1], sentLevels);
        Assert.Equal(0.2,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);

        releaseFirst.TrySetResult();
        await client.WaitForMutationQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        lock (sentLevels)
            Assert.Equal([0.1, 0.2], sentLevels);
    }

    [Fact]
    public async Task OlderPairEchoCannotReplaceLatestOptimisticIntent()
    {
        using var client = CreateClient();
        LoadChannel(client);

        client.SetChannelMix("mic", "vc", level: 0.2);
        client.SetChannelMix("mic", "vc", level: 0.8);
        PatchMixLevel(client, 0.8);
        PatchMixLevel(client, 0.2);

        Assert.Equal(0.8,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);

        PatchMixLevel(client, 0.6);
        Assert.Equal(0.6,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);
        await client.WaitForMutationQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedPairSendReleasesEchoProtection()
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new WaveLinkClient(async (_, _) =>
        {
            sendStarted.TrySetResult();
            await releaseSend.Task;
            throw new InvalidOperationException("send failed");
        });
        LoadChannel(client);

        client.SetChannelMix("mic", "vc", level: 0.8);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        PatchMixLevel(client, 0.3);
        Assert.Equal(0.8,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);

        releaseSend.TrySetResult();
        await client.WaitForMutationQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        PatchMixLevel(client, 0.4);
        Assert.Equal(0.4,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);
    }

    [Fact]
    public void DisconnectReleasesEchoProtection()
    {
        using var client = CreateClient();
        LoadChannel(client);
        client.SetChannelMix("mic", "vc", level: 0.8);

        client.FailPending();
        PatchMixLevel(client, 0.4);

        Assert.Equal(0.4,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);
    }

    [Fact]
    public void FullChannelReplacementReleasesEchoProtectionWithoutMutatingOptimisticSnapshot()
    {
        using var client = CreateClient();
        LoadChannel(client);
        client.SetChannelMix("mic", "vc", level: 0.8);
        var optimistic = Assert.Single(client.GetChannels());

        LoadChannel(client, mixLevel: 0.3);
        PatchMixLevel(client, 0.4);

        Assert.Equal(0.8, Assert.Single(optimistic.Mixes).Level);
        Assert.Equal(0.4,
            Assert.Single(Assert.Single(client.GetChannels()).Mixes).Level);
    }

    private static WaveLinkClient CreateClient() =>
        new((_, _) => Task.CompletedTask);

    private static void LoadChannel(WaveLinkClient client, double mixLevel = 0.25)
    {
        client.HandleMessage($$$"""
            {"method":"channelsChanged","params":{"channels":[
              {"id":"mic","name":"Microphone","level":0.5,"isMuted":false,
               "mixes":[{"id":"vc","level":{{{mixLevel}}},"isMuted":false}]}
            ]}}
            """);
    }

    private static void PatchMixLevel(WaveLinkClient client, double level) =>
        PatchChannel(client, new JsonObject
        {
            ["id"] = "mic",
            ["mixes"] = new JsonArray(new JsonObject
            {
                ["id"] = "vc",
                ["level"] = level
            })
        }.ToJsonString());

    private static void PatchChannel(WaveLinkClient client, string patch)
    {
        client.HandleMessage(new JsonObject
        {
            ["method"] = "channelChanged",
            ["params"] = JsonNode.Parse(patch)
        }.ToJsonString());
    }
}
