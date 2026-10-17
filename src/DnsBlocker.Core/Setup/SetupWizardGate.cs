using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Setup;

/// <summary>
/// Decides whether the first-run / incomplete-config wizard should open.
/// Coordinated with install tooling via <see cref="AppPaths.SetupCompleteFlagPath"/>.
/// </summary>
public static class SetupWizardGate
{
    public static bool ShouldShowWizard(AppConfig? config, bool force = false)
    {
        if (force)
            return true;

        if (config is null)
            return true;

        if (!config.SetupWizardCompleted && !AppPaths.SetupCompleteFlagExists())
            return true;

        if (IsConfigIncomplete(config))
            return true;

        return false;
    }

    public static bool IsConfigIncomplete(AppConfig config)
    {
        var targets = config.DnsTargets?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList() ?? [];

        if (targets.Count == 0)
            return true;

        // Default scaffold only — not a real farm yet
        if (targets.All(IsLocalOnlyTarget))
            return true;

        // Curated-lists-only (empty Steve Jenkins LoL URL) is a valid DC-friendly config.
        if (string.IsNullOrWhiteSpace(config.BlocklistsUrl) && !HasAnyBlocklistSource(config))
            return true;

        return false;
    }

    /// <summary>True when curated toggles or custom ExtraBlocklistUrls supply at least one source.</summary>
    public static bool HasAnyBlocklistSource(AppConfig config)
    {
        if (config.CuratedYoutubeAds || config.CuratedOisdBig || config.CuratedOisdNsfw || config.CuratedChadmayfieldPorn)
            return true;
        return config.ExtraBlocklistUrls is { Count: > 0 }
               && config.ExtraBlocklistUrls.Any(u => !string.IsNullOrWhiteSpace(u));
    }

    public static bool IsLocalOnlyTarget(string target)
    {
        var t = target.Trim().ToLowerInvariant();
        return t is "localhost" or "127.0.0.1" or "::1" or "(local)" or ".";
    }

    public static void MarkCompleted(AppConfig config)
    {
        config.SetupWizardCompleted = true;
        AppPaths.MarkSetupComplete();
    }

    public static void ResetForRerun(AppConfig config)
    {
        config.SetupWizardCompleted = false;
        AppPaths.ClearSetupComplete();
    }
}
