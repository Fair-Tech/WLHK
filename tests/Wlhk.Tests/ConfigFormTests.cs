using System.Runtime.ExceptionServices;
using Wlhk.Core;
using Wlhk.UI;
using Wlhk.WaveLink;
using Xunit;

namespace Wlhk.Tests;

public sealed class ConfigFormTests
{
    [Fact]
    public void FirstChannelInitializesMixChoicesOnFirstRender()
    {
        RunInSta(() =>
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "WLHK.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var client = new WaveLinkClient((_, _) => Task.CompletedTask);
                client.HandleMessage("""
                    {"method":"channelsChanged","params":{"channels":[
                      {"id":"mic","name":"Microphone","level":0.5,"isMuted":false,
                       "mixes":[{"id":"vc","level":0.4,"isMuted":false}]}
                    ]}}
                    """);
                client.HandleMessage("""
                    {"method":"mixesChanged","params":{"mixes":[
                      {"id":"vc","name":"VC","level":0.4,"isMuted":false}
                    ]}}
                    """);

                var store = new ConfigStore(Path.Combine(tempDir, "config.json"));
                var action = new HotkeyAction { Type = "mute_channel" };
                store.Current.Hotkeys["CTRL+M"] = new HotkeyBinding { NormalAction = action };
                using var hook = new KeyboardHook();
                using var form = new ConfigForm(
                    store, client, hook, () => { }, () => { }, () => { },
                    SystemIcons.Application);

                var targetMixLabel = Descendants(form).OfType<Label>()
                    .Single(label => label.Text == "Target Mix");
                var targetMix = targetMixLabel.Parent!.Controls.OfType<ComboBox>()
                    .Single(combo => combo.Left == targetMixLabel.Left);

                Assert.Equal("mic", action.ChannelId);
                Assert.Equal("mic", store.Snapshot.Hotkeys["CTRL+M"].NormalAction!.ChannelId);
                Assert.Equal(["All Mixes", "VC"], targetMix.Items.Cast<object>()
                    .Select(item => item.ToString()!).ToArray());
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
