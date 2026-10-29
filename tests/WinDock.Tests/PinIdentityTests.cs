using WinDock.Models;
using WinDock.Services;

namespace WinDock.Tests;

public class PinIdentityTests
{
    const string ChromeProxy = @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe";
    const string Explorer = @"C:\Windows\explorer.exe";

    [Fact]
    public void Reload_keeps_both_Chrome_web_apps_sharing_chrome_proxy()
    {
        var pins = new[]
        {
            (Item: new PinItem { Name = "GitHub", Target = @"C:\Links\GitHub.lnk", AppId = "Chrome._crx_github" }, Args: "--profile-directory=Default --app-id=github"),
            (Item: new PinItem { Name = "DeepSeek Harness", Target = @"C:\Links\DeepSeek Harness.lnk", AppId = "Chrome._crx_deepseek" }, Args: "--profile-directory=Default --app-id=deepseek")
        };
        var seen = new HashSet<string>();

        var retained = pins.Where(pin => PinIdentity.TryAdd(seen, pin.Item, ChromeProxy, pin.Args)).Select(pin => pin.Item.Name).ToArray();

        Assert.Equal(["GitHub", "DeepSeek Harness"], retained);
    }

    [Fact]
    public void Different_shortcut_app_ids_do_not_collapse_to_the_shared_executable()
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "First", Target = @"C:\Links\First.lnk" };
        var second = new PinItem { Name = "Second", Target = @"C:\Links\Second.lnk" };

        Assert.True(PinIdentity.TryAdd(seen, first, ChromeProxy, appId: "Chrome._crx_first"));
        Assert.True(PinIdentity.TryAdd(seen, second, ChromeProxy, appId: "Chrome._crx_second"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Chrome")]
    public void Shortcuts_with_different_arguments_are_kept_even_with_a_shared_app_id(string? appId)
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "First", Target = @"C:\Links\First.lnk", AppId = appId };
        var second = new PinItem { Name = "Second", Target = @"C:\Links\Second.lnk", AppId = appId };

        Assert.True(PinIdentity.TryAdd(seen, first, ChromeProxy, "--app=https://example.test/First"));
        Assert.True(PinIdentity.TryAdd(seen, second, ChromeProxy, "--app=https://example.test/Second"));
    }

    [Fact]
    public void Argument_values_keep_meaningful_case()
    {
        var seen = new HashSet<string>();
        var item = new PinItem { Name = "Browser", Target = @"C:\Links\Browser.lnk", AppId = "Chrome" };

        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, "--app=https://example.test/Project"));
        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, "--app=https://example.test/project"));
        Assert.False(PinIdentity.TryAdd(seen, item, ChromeProxy, "--app=https://example.test/Project"));
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(4095)]
    [InlineData(16383)]
    public void Long_arguments_with_a_shared_prefix_keep_their_distinct_tails(int sharedLength)
    {
        var seen = new HashSet<string>();
        var item = new PinItem { Name = "Browser", Target = @"C:\Links\Browser.lnk", AppId = "Chrome" };
        var prefix = "--app=https://example.test/".PadRight(sharedLength, 'x');
        var first = prefix + "/Project";
        var second = prefix + "/project";

        Assert.Equal(first[..1023], second[..1023]);
        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, first));
        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, second));
        Assert.False(PinIdentity.TryAdd(seen, item, ChromeProxy, first));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" --app=https://example.test/Project ")]
    public void Whitespace_arguments_are_preserved_exactly(string arguments)
    {
        var seen = new HashSet<string>();
        var item = new PinItem { Name = "Browser", Target = @"C:\Links\Browser.lnk", AppId = "Chrome" };

        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, ""));
        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, arguments));
        Assert.False(PinIdentity.TryAdd(seen, item, ChromeProxy, arguments));
    }

    [Fact]
    public void Unconfirmed_launch_metadata_keeps_different_shortcut_paths_despite_shared_app_id_and_target()
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "Browser", Target = @"C:\Links\First.lnk", AppId = "Chrome" };
        var second = new PinItem { Name = "Browser", Target = @"D:\Links\Second.lnk", AppId = "Chrome" };

        Assert.True(PinIdentity.TryAdd(seen, first, ChromeProxy, launchKnown: false));
        Assert.True(PinIdentity.TryAdd(seen, second, ChromeProxy, launchKnown: false));
        Assert.False(PinIdentity.TryAdd(seen, new PinItem { Target = first.Target.ToUpperInvariant() }, ChromeProxy, launchKnown: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_arguments_do_not_alias_confirmed_empty_arguments(bool unknownFirst)
    {
        var seen = new HashSet<string>();
        var unknown = new PinItem { Name = "Browser", Target = @"C:\Links\Unknown.lnk", AppId = "Chrome" };
        var empty = new PinItem { Name = "Browser", Target = @"C:\Links\Empty.lnk", AppId = "Chrome" };

        if (unknownFirst)
        {
            Assert.True(PinIdentity.TryAdd(seen, unknown, ChromeProxy, launchKnown: false));
            Assert.True(PinIdentity.TryAdd(seen, empty, ChromeProxy, ""));
        }
        else
        {
            Assert.True(PinIdentity.TryAdd(seen, empty, ChromeProxy, ""));
            Assert.True(PinIdentity.TryAdd(seen, unknown, ChromeProxy, launchKnown: false));
        }
    }

    [Theory]
    [InlineData("steam://rungameid/1")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void Raw_and_launcher_identity_remains_app_id_only_when_metadata_is_unconfirmed(string arguments)
    {
        var seen = new HashSet<string>();
        var raw = new PinItem { Name = "Raw", AppId = "App", Favorites = "", Resolve = "" };
        var launcher = new PinItem { Name = "Launcher", Target = Explorer, Args = arguments, AppId = "APP" };

        Assert.True(PinIdentity.TryAdd(seen, raw, launchKnown: false));
        Assert.False(PinIdentity.TryAdd(seen, launcher, launchKnown: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--profile-directory=Default --app-id=github")]
    public void Renamed_shortcuts_to_the_same_launch_target_are_duplicates(string? arguments)
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "GitHub", Target = @"C:\Links\GitHub.lnk", AppId = "Chrome._crx_github" };
        var renamed = new PinItem { Name = "GitHub copy", Target = @"D:\Links\Renamed.lnk", AppId = "chrome._crx_GITHUB" };

        Assert.True(PinIdentity.TryAdd(seen, first, ChromeProxy, arguments));
        Assert.False(PinIdentity.TryAdd(seen, renamed, ChromeProxy.ToUpperInvariant(), arguments));
    }

    [Fact]
    public void A_native_executable_and_an_unidentified_shortcut_to_it_are_duplicates()
    {
        var seen = new HashSet<string>();
        var executable = new PinItem { Name = "Editor", Target = @"C:\Apps\Editor.exe" };
        var shortcut = new PinItem { Name = "Editor shortcut", Target = @"C:\Links\Editor.lnk" };

        Assert.True(PinIdentity.TryAdd(seen, executable, executable.Target));
        Assert.False(PinIdentity.TryAdd(seen, shortcut, executable.Target.ToUpperInvariant(), ""));
    }

    [Fact]
    public void A_shortcut_with_an_explicit_app_id_is_kept_beside_the_native_executable()
    {
        var seen = new HashSet<string>();
        var executable = new PinItem { Name = "Browser", Target = ChromeProxy };
        var shortcut = new PinItem { Name = "Web app", Target = @"C:\Links\Web app.lnk" };

        Assert.True(PinIdentity.TryAdd(seen, executable, ChromeProxy));
        Assert.True(PinIdentity.TryAdd(seen, shortcut, ChromeProxy, appId: "Chrome._crx_webapp"));
    }

    [Fact]
    public void A_parameterized_shortcut_is_kept_beside_the_same_app_without_arguments()
    {
        var seen = new HashSet<string>();
        var item = new PinItem { Name = "Browser", Target = @"C:\Links\Browser.lnk", AppId = "Chrome" };

        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy));
        Assert.True(PinIdentity.TryAdd(seen, item, ChromeProxy, "--app=https://example.test/Project"));
        Assert.False(PinIdentity.TryAdd(seen, item, ChromeProxy, ""));
        Assert.False(PinIdentity.TryAdd(seen, item, ChromeProxy, "--app=https://example.test/Project"));
    }

    [Fact]
    public void Unidentified_shortcuts_to_different_executables_are_kept()
    {
        var seen = new HashSet<string>();
        var item = new PinItem { Name = "App", Target = @"C:\Links\App.lnk" };

        Assert.True(PinIdentity.TryAdd(seen, item, @"C:\Apps\First.exe"));
        Assert.True(PinIdentity.TryAdd(seen, item, @"C:\Apps\Second.exe"));
    }

    [Fact]
    public void Shortcut_metadata_keeps_the_imported_app_id_authoritative()
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "GitHub", Target = @"C:\Links\GitHub.lnk", AppId = "Chrome._crx_github" };
        var second = new PinItem { Name = "DeepSeek", Target = @"C:\Links\DeepSeek.lnk", AppId = "Chrome._crx_deepseek" };

        Assert.True(PinIdentity.TryAdd(seen, first, ChromeProxy, appId: "Chrome"));
        Assert.True(PinIdentity.TryAdd(seen, second, ChromeProxy, appId: "Chrome"));
    }

    [Theory]
    [InlineData("steam://rungameid/1", "steam://rungameid/2", "WinDock.Steam.1", "WinDock.Steam.2")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"shell:AppsFolder\Microsoft.WindowsStore_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsStore_8wekyb3d8bbwe!App")]
    public void Uri_and_Store_launchers_keep_their_existing_app_id_identity(string firstArgs, string secondArgs, string firstId, string secondId)
    {
        var seen = new HashSet<string>();
        var first = new PinItem { Name = "First", Target = Explorer, Args = firstArgs, AppId = firstId };
        var second = new PinItem { Name = "Second", Target = Explorer, Args = secondArgs, AppId = secondId };
        var duplicate = new PinItem { Name = "Renamed", Target = Explorer, Args = firstArgs, AppId = firstId.ToUpperInvariant() };

        Assert.True(PinIdentity.TryAdd(seen, first));
        Assert.True(PinIdentity.TryAdd(seen, second));
        Assert.False(PinIdentity.TryAdd(seen, duplicate));
    }

    [Fact]
    public void Raw_and_Store_launcher_representations_of_the_same_app_remain_duplicates()
    {
        var seen = new HashSet<string>();
        const string appId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        var raw = new PinItem { Name = "Raw", AppId = appId, Favorites = "", Resolve = "" };
        var launcher = new PinItem { Name = "Calculator", Target = Explorer, Args = @"shell:AppsFolder\" + appId, AppId = appId.ToUpperInvariant() };

        Assert.True(PinIdentity.TryAdd(seen, raw));
        Assert.False(PinIdentity.TryAdd(seen, launcher));
    }

    [Fact]
    public void Raw_and_native_shortcut_representations_with_the_same_app_id_remain_duplicates()
    {
        var seen = new HashSet<string>();
        var raw = new PinItem { Name = "Raw", AppId = "Editor", Favorites = "", Resolve = "" };
        var shortcut = new PinItem { Name = "Editor", Target = @"C:\Links\Editor.lnk" };

        Assert.True(PinIdentity.TryAdd(seen, raw));
        Assert.False(PinIdentity.TryAdd(seen, shortcut, @"C:\Apps\Editor.exe", appId: "EDITOR"));
    }

    [Fact]
    public void Unresolved_items_and_raw_items_without_ids_still_fall_back_to_names()
    {
        var seen = new HashSet<string>();

        Assert.True(PinIdentity.TryAdd(seen, new PinItem { Name = "Unknown" }));
        Assert.False(PinIdentity.TryAdd(seen, new PinItem { Name = "UNKNOWN", AppId = "", Favorites = "", Resolve = "" }));
    }
}
