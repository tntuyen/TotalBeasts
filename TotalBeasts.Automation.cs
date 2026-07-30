using System;
using System.Diagnostics;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using Vector2 = System.Numerics.Vector2;

namespace TotalBeasts;

public partial class TotalBeasts
{
    private SyncTask<bool> _automationTask;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private ServerInventory _automationInventory;

    private int _nextActionDelayMs;
    private readonly Random _random = new();

    /// <summary>
    /// Checks whether a beast should be itemized (true) or released (false).
    /// Price wins first: a beast worth the threshold is itemized even when it
    /// is a yellow (some league-mechanic yellows sell for real money). The
    /// yellow toggle then only forces itemizing the cheap generic captures.
    /// </summary>
    private bool ShouldItemizeBeast(CachedBeastEntry entry, int threshold)
    {
        if (entry.Price >= threshold) return true;
        if (entry.IsGenericYellow) return Settings.Automation.ItemizeYellowBeasts.Value;
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Yields frames until <paramref name="ms"/> milliseconds have passed.
    /// More accurate than counting frames; does not block the render thread.
    /// </summary>
    private static async SyncTask<bool> WaitMs(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
            await TaskUtils.NextFrame();
        return true;
    }

    /// <summary>
    /// Polls until the beast list count decreases, confirming the server processed the
    /// release/itemize (WTC -- Wait To Confirm). Refreshes the beast cache each frame
    /// so the caller gets fresh data on success. <c>IsVisible</c> on individual beast
    /// elements does NOT flip when the game removes them -- count-based detection is
    /// the only reliable method.
    /// Returns <c>true</c> if count decreased (confirmed), <c>false</c> on timeout.
    /// </summary>
    private async SyncTask<bool> WaitForBeastCountChange(int countBefore, int timeoutMs = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await TaskUtils.NextFrame();
            RefreshBeastCache();
            _beastCacheTimer.Restart();
            if (_cachedBeasts.Count < countBefore) return true;
        }
        return false; // timed out -- server did not confirm in time
    }

    /// <summary>
    /// Returns a random click position within the center 60% of <paramref name="rect"/>.
    /// </summary>
    private Vector2 GetRandomClickPos(SharpDX.RectangleF rect)
    {
        var marginX = rect.Width  * 0.20f;
        var marginY = rect.Height * 0.20f;
        var x = rect.Left + marginX + (float)(_random.NextDouble() * (rect.Width  - marginX * 2));
        var y = rect.Top  + marginY + (float)(_random.NextDouble() * (rect.Height - marginY * 2));
        return new Vector2(x, y);
    }

    // ── Inventory space check ─────────────────────────────────────────────────

    /// <summary>Returns true if at least one free 1×1 slot exists in the player inventory.</summary>
    private bool HasInventorySpace()
    {
        var inv = _automationInventory;
        if (inv == null) return false;

        var rows = inv.Rows;
        var cols = inv.Columns;
        if (rows <= 0 || cols <= 0) return false;

        var grid = new bool[rows, cols];
        try
        {
            foreach (var item in inv.InventorySlotItems)
            {
                var endY = Math.Min(rows, item.PosY + item.SizeY);
                var endX = Math.Min(cols, item.PosX + item.SizeX);
                for (var y = Math.Max(0, item.PosY); y < endY; y++)
                for (var x = Math.Max(0, item.PosX); x < endX; x++)
                    grid[y, x] = true;
            }
        }
        catch { return false; }

        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            if (!grid[y, x]) return true;

        return false;
    }

    // ── Click helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// True while the tile still shows the beast we cached. The bestiary grid
    /// re-binds tile slots 1-2 frames after the server confirms an action, so
    /// both the position AND the identity measured at cache time can go stale
    /// between deciding to click and the physical click.
    /// </summary>
    private bool BeastStillMatches(CapturedBeast beast, string expectedName)
    {
        try { return ReadBeastName(beast) == expectedName; }
        catch { return false; }
    }

    /// <summary>
    /// Ctrl+clicks a beast-tile button, re-verifying geometry and identity as
    /// late as possible. Returns false WITHOUT clicking when the slot re-bound
    /// or moved under us -- the caller must re-cache and pick a fresh target.
    /// </summary>
    private async SyncTask<bool> CtrlClickBeastButton(CapturedBeast beast, Element button, string expectedName)
    {
        if (button == null) return false;

        var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
        var clickPos = GetRandomClickPos(button.GetClientRect()) + windowOffset;

        bool ok = Settings.Automation.UseInputHumanizer.Value
            ? await CtrlClickViaHumanizer(beast, button, expectedName, clickPos, windowOffset)
            : await CtrlClickSimple(beast, button, expectedName, clickPos, windowOffset);

        if (!ok) return false;

        _sinceLastClick.Restart();
        return true;
    }

    private async SyncTask<bool> CtrlClickSimple(CapturedBeast beast, Element button, string expectedName, Vector2 clickPos, Vector2 windowOffset)
    {
        Input.SetCursorPos(clickPos);
        await WaitMs(Settings.Automation.PreClickDelayMs.Value);

        // The grid may have reflowed during the pre-click delay (the previous
        // action's visual removal can land 1-2 frames after its WTC confirm).
        // If the button moved out from under the cursor, re-aim once and give
        // the layout a frame to settle.
        try
        {
            var rect = button.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            if (!rect.Contains(clickPos.X - windowOffset.X, clickPos.Y - windowOffset.Y))
            {
                clickPos = GetRandomClickPos(rect) + windowOffset;
                Input.SetCursorPos(clickPos);
                await TaskUtils.NextFrame();
            }
        }
        catch { return false; }

        // Last-moment identity check, ~2 frames from the actual click.
        if (!BeastStillMatches(beast, expectedName)) return false;

        Input.KeyDown(Keys.ControlKey);
        await TaskUtils.NextFrame();
        Input.Click(MouseButtons.Left);
        await TaskUtils.NextFrame();
        Input.KeyUp(Keys.ControlKey);
        return true;
    }

    private async SyncTask<bool> CtrlClickViaHumanizer(CapturedBeast beast, Element button, string expectedName, Vector2 clickPos, Vector2 windowOffset)
    {
        var getController = GameController.PluginBridge
            .GetMethod<Func<string, TimeSpan, SyncTask<object>>>("InputHumanizer.GetInputController");

        if (getController == null)
        {
            LogError("InputHumanizer plugin not available -- switch to SimpleDelay or enable InputHumanizer.");
            Settings.Automation.Enable.Value = false;
            return false;
        }

        dynamic controller = await getController("TotalBeasts", TimeSpan.FromMilliseconds(500));
        if (controller == null)
        {
            LogError("InputHumanizer busy -- another plugin holds the input lock.");
            return false;
        }

        try
        {
            // Aim at the freshest rect and re-verify identity right before the
            // humanized click -- the movement itself adds delay during which
            // the grid may re-bind. (The residual window during the humanized
            // motion cannot be closed from this side.)
            try
            {
                var rect = button.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0) return false;
                clickPos = GetRandomClickPos(rect) + windowOffset;
            }
            catch { return false; }

            if (!BeastStillMatches(beast, expectedName)) return false;

            controller.KeyDown(Keys.ControlKey);
            await controller.Click(clickPos);
            await controller.KeyUp(Keys.ControlKey, true);
        }
        finally
        {
            controller.Dispose();
        }

        return true;
    }

    // ── Main automation loop ──────────────────────────────────────────────────

    private async SyncTask<bool> RunAutomationAsync()
    {
        var cfg = Settings.Automation;
        var loopSw = Stopwatch.StartNew();
        var verifyFailures = 0;

        // Outer loop: after each confirmed beast, refresh the cache and immediately
        // look for the next one -- no task restart overhead or stale-cache delay.
        while (true)
        {
            if (!GameController.Window.IsForeground()) return true;
            if (!Settings.Enable.Value) return true;

            // WTC fallback: only delays if the previous action timed out without server confirmation.
            if (_sinceLastClick.ElapsedMilliseconds < _nextActionDelayMs)
                return true;

            // A timed-out click may still resolve server-side during the fallback
            // delay, re-binding the grid slots. Never act on the pre-timeout cache.
            if (_beastCacheDirty)
            {
                RefreshBeastCache();
                _beastCacheTimer.Restart();
            }

            // Use the render-layer cache -- avoids re-reading beast addresses from memory.
            if (!_bestiaryVisible || _cachedBeasts.Count == 0) return true;

            int threshold = cfg.ItemizeAboveChaos.Value;

            // If inventory is full, stop -- nothing to do until the player makes room.
            if (cfg.CheckInventoryBeforeItemize.Value && !HasInventorySpace())
            {
                Settings.Automation.Enable.Value = false;
                return true;
            }

            // Only interact with beasts inside the visible scroll area of the panel.
            var viewTop    = _cachedPanelRect.Top;
            var viewBottom = _cachedPanelRect.Bottom;

            // ── Look-ahead: count releases before next itemize target ─────────
            // -1 = no itemize target visible (all releases -- full speed)
            //  0 = first visible beast IS the itemize target (CAREFUL)
            //  1-2 = approaching an itemize target (SLOW)
            //  3+ = far away (FAST)
            int releasesBeforeItemize = -1;
            foreach (var scan in _cachedBeasts)
            {
                try
                {
                    var scanRect = scan.Element.GetClientRect();
                    if (scanRect.Width <= 0 || scanRect.Height <= 0) continue;
                    if (scanRect.Bottom < viewTop || scanRect.Top > viewBottom) continue;

                    if (ShouldItemizeBeast(scan, threshold))
                    {
                        if (releasesBeforeItemize < 0) releasesBeforeItemize = 0;
                        break;
                    }
                    releasesBeforeItemize = releasesBeforeItemize < 0 ? 1 : releasesBeforeItemize + 1;
                }
                catch { }
            }

            // ── Process the first visible beast ───────────────────────────────
            bool clickedAny = false;
            var countBefore = _cachedBeasts.Count;

            foreach (var entry in _cachedBeasts)
            {
                try
                {
                    var rect = entry.Element.GetClientRect();
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    if (rect.Bottom < viewTop || rect.Top > viewBottom) continue;

                    bool shouldItemize = ShouldItemizeBeast(entry, threshold);

                    // CAREFUL zone: before clicking an itemize target, verify the
                    // element still matches what we cached. If the UI shifted and
                    // this address now points to a different beast, re-cache instead
                    // of risking a misclick on a valuable beast.
                    if (shouldItemize)
                    {
                        var liveName = ReadBeastName(entry.Element);
                        if (liveName != entry.DisplayName)
                        {
                            RefreshBeastCache();
                            _beastCacheTimer.Restart();
                            clickedAny = true; // force while loop restart
                            break;
                        }
                    }

                    var btn = shouldItemize ? entry.Element[0] : entry.Element.ReleaseButton;
                    if (btn == null) continue;

                    string zone = shouldItemize ? "CAREFUL"
                        : releasesBeforeItemize >= 0 && releasesBeforeItemize <= 2
                            ? $"SLOW({releasesBeforeItemize})"
                            : "FAST";

                    loopSw.Restart();
                    if (!await CtrlClickBeastButton(entry.Element, btn, entry.DisplayName))
                    {
                        // Slot re-bound or moved between caching and clicking --
                        // re-cache and restart instead of clicking a different beast.
                        RefreshBeastCache();
                        _beastCacheTimer.Restart();
                        if (++verifyFailures >= 3)
                        {
                            LogMsg("[Beast] click verification failed 3x -- backing off");
                            _nextActionDelayMs = cfg.FallbackDelayMs.Value;
                            return true;
                        }
                        clickedAny = true;
                        break;
                    }
                    verifyFailures = 0;
                    var clickMs = loopSw.ElapsedMilliseconds;

                    // WTC: poll cache refresh until beast count decreases.
                    var wtcSw = Stopwatch.StartNew();
                    var confirmed = await WaitForBeastCountChange(countBefore);
                    var wtcMs = wtcSw.ElapsedMilliseconds;
                    var ping = GameController.IngameState.ServerData.Latency;

                    if (confirmed)
                    {
                        _nextActionDelayMs = 0;
                        // Cache is already refreshed inside WaitForBeastCountChange.
                        LogMsg($"[Beast] click={clickMs}ms wtc={wtcMs}ms ping={ping}ms zone={zone} cache={_cachedBeasts.Count} remaining");

                        // SLOW zone: approaching an itemize target. Pause a few frames
                        // to let the game's input hit-testing catch up with the UI shift
                        // before we click the valuable beast.
                        if (releasesBeforeItemize >= 0 && releasesBeforeItemize <= 2 && !shouldItemize)
                        {
                            await TaskUtils.NextFrame();
                            await TaskUtils.NextFrame();
                            // Re-cache after settle to get fully updated positions.
                            RefreshBeastCache();
                            _beastCacheTimer.Restart();
                        }

                        clickedAny = true;
                        break; // restart foreach with fresh _cachedBeasts
                    }
                    else
                    {
                        LogMsg($"[Beast] click={clickMs}ms wtc=TIMEOUT({wtcMs}ms) ping={ping}ms zone={zone} -- applying fallback delay");
                        _nextActionDelayMs = Settings.Automation.FallbackDelayMs.Value;
                        // The click may still land during the fallback delay --
                        // force a cache rebuild before the next run acts.
                        _beastCacheDirty = true;
                        return true;
                    }
                }
                catch { /* element read failure -- skip beast */ }
            }

            if (!clickedAny) break; // no more actionable beasts
        }

        return true;
    }
}
