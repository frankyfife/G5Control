using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace G5Control;

/// <param name="DelayMs">Pause after the step; the TV needs time after mode and energy-saving changes.</param>
/// <param name="Resyncs">Step makes the TV resync the HDMI signal; it runs first and is not repeated on retries.</param>
public sealed record Step(string Key, string Value, string Category = "picture", int DelayMs = 150, bool Resyncs = false);

public sealed record Preset(string Name, string Description, string Glyph, IReadOnlyList<Step> Steps)
{
    // Picture values the TV lets us read back, used to check that the preset really landed.
    static readonly string[] VerifiableKeys = ["pictureMode", "backlight", "energySaving"];

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await using var tv = await LgTv.ConnectAsync(ct);
        // Picture settings belong to the current picture mode, and an HDR signal only offers the HDR modes,
        // so "game" has to become "hdrGame" while the PC sends HDR.
        var hdr = IsHdr((await tv.GetPictureSettingsAsync(ct))["pictureMode"]?.ToString());
        var steps = Steps.Select(s => s.Key == "pictureMode" ? s with { Value = ForSignal(s.Value, hdr) } : s).ToList();
        foreach (var step in steps)
        {
            await tv.SetSettingAsync(step.Key, step.Value, step.Category, ct);
            await Task.Delay(step.DelayMs, ct);
        }

        // While the signal resyncs (VRR/Game Optimizer changes), writes can land in another picture mode
        // context, and energySaving "max" briefly locks the backlight. Re-apply the picture steps until it sticks.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(2000, ct);
            if (Matches(await tv.GetPictureSettingsAsync(ct)))
            {
                return;
            }
            foreach (var step in steps.Where(s => !s.Resyncs))
            {
                await tv.SetSettingAsync(step.Key, step.Value, step.Category, ct);
                await Task.Delay(step.DelayMs, ct);
            }
        }
        throw new InvalidOperationException($"{Name}: Der Fernseher hat nicht alle Werte übernommen.");
    }

    /// <summary>Whether the TV's readable picture values are the ones this preset sets.</summary>
    public bool Matches(JsonObject actual)
    {
        var hdr = IsHdr(actual["pictureMode"]?.ToString());
        return Steps.Where(s => VerifiableKeys.Contains(s.Key))
            .GroupBy(s => s.Key)
            .Select(g => g.Last())
            .All(s => actual[s.Key]?.ToString() == (s.Key == "pictureMode" ? ForSignal(s.Value, hdr) : s.Value));
    }

    static bool IsHdr(string? pictureMode) => pictureMode?.StartsWith("hdr", StringComparison.Ordinal) == true;

    static string ForSignal(string pictureMode, bool hdr) =>
        hdr && !IsHdr(pictureMode) ? "hdr" + char.ToUpperInvariant(pictureMode[0]) + pictureMode[1..] : pictureMode;
}

public static class Presets
{
    // Undo what "Minimal" changes, so the other presets always end in the normal game picture mode.
    static readonly Step[] Restore =
    [
        new("energySaving", "off", DelayMs: 1500),
        new("darkMode", "off", "other"),
        new("pictureMode", "game", DelayMs: 2500),
    ];

    static Step Vrr(string value) => new("gameOptimization", value, "other", DelayMs: 3000, Resyncs: true);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("Gaming", "Game Optimizer, VRR/G-Sync, Input-Lag Boost, Helligkeit 100", "",
        [
            Vrr("on"),
            new("gameMode_hdmi1", "on", "other", DelayMs: 1500, Resyncs: true),
            new("inputOptimization", "boost", "other", DelayMs: 1500, Resyncs: true),
            .. Restore,
            new("backlight", "100"),
        ]),
        new("Desktop Tag", "Helligkeit 90, VRR an", "",
        [
            Vrr("on"),
            .. Restore,
            new("backlight", "90"),
        ]),
        new("Desktop Nacht", "Helligkeit 45, VRR an", "",
        [
            Vrr("on"),
            .. Restore,
            new("backlight", "45"),
        ]),
        // No switch to "eco": energy saving max plus backlight 0 already is the minimum, and HDMI1 would remember eco.
        new("Minimal", "Helligkeit 0, Dark Room Stufe 2, Energiesparen max, VRR aus", "",
        [
            Vrr("off"),
            new("peakBrightness", "off"),
            new("energySaving", "max", DelayMs: 3000),
            new("darkMode", "level2", "other", DelayMs: 500),
            new("backlight", "0"),
        ]),
    ];

    static readonly string ActiveFile = Path.Combine(LgTv.DataDir, "active.txt");

    public static string? LoadActive()
    {
        try
        {
            return File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void SaveActive(string name)
    {
        Directory.CreateDirectory(LgTv.DataDir);
        File.WriteAllText(ActiveFile, name);
    }
}
