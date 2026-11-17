using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Dalamud.Configuration;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

/// <summary>
/// Validation feedback for a dev plugin location.
/// </summary>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal enum DevPluginLocationError
{
    /// <summary>The operation found no validation error.</summary>
    None,

    /// <summary>The filesystem check has not finished.</summary>
    Pending,

    /// <summary>The DLL is missing or inaccessible.</summary>
    DllUnavailable,

    /// <summary>The path is not rooted or does not have the case-sensitive .dll extension.</summary>
    InvalidPath,

    /// <summary>The manifest is missing or inaccessible.</summary>
    ManifestUnavailable,

    /// <summary>Another location already uses the path.</summary>
    Duplicate,
}

/// <summary>
/// Manages dev plugin location drafts, submitted edits, and accepted settings on the UI thread.
/// </summary>
/// <remarks>Submitted paths replace accepted paths only after a successful filesystem check.</remarks>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal sealed class DevPluginLocationEditor
{
    private readonly DevPluginPathValidationCache cache;
    private readonly List<DevPluginLocationSettings> locations = [];
    private readonly Dictionary<DevPluginLocationSettings, string> drafts = [];
    private readonly Dictionary<DevPluginLocationSettings, int> inputRevisions = [];
    private readonly Dictionary<DevPluginLocationSettings, PendingEdit> pending = [];
    private readonly Dictionary<DevPluginLocationSettings, (string Path, DevPluginLocationError Error)> rejected = [];

    public DevPluginLocationEditor(DevPluginPathValidationCache? cache = null)
    {
        this.cache = cache ?? new DevPluginPathValidationCache();
    }

    /// <summary>Gets the accepted locations for the current settings session.</summary>
    public IReadOnlyList<DevPluginLocationSettings> Locations => this.locations;

    /// <summary>Gets a value indicating whether an add, removal, or accepted path edit is unsaved.</summary>
    public bool Changed { get; private set; }

    /// <summary>Gets a value indicating whether submitted path edits are still awaiting validation.</summary>
    /// <remarks>Applies completed results without starting checks.</remarks>
    public bool HasPendingEdits
    {
        get
        {
            this.ConsumeCompleted();
            return this.pending.Count != 0;
        }
    }

    /// <summary>Replaces the session with cloned settings and invalidates outstanding edits and observations.</summary>
    /// <param name="settings">The configured locations.</param>
    public void Reset(IEnumerable<DevPluginLocationSettings> settings)
    {
        this.locations.Clear();
        this.locations.AddRange(settings.Select(location => location.Clone()));
        this.drafts.Clear();
        this.inputRevisions.Clear();
        this.pending.Clear();
        this.rejected.Clear();
        this.Changed = false;
        this.cache.Reset();
        this.UpdatePaths();
    }

    public void MarkSaved() => this.Changed = false;

    /// <summary>Refreshes filesystem warnings and applies completed path edits.</summary>
    public void Refresh()
    {
        this.cache.Refresh();
        this.ConsumeCompleted();
    }

    /// <summary>Advances submitted edits without refreshing unrelated filesystem warnings.</summary>
    public void AdvancePendingEdits()
    {
        this.cache.PumpPending(this.pending.Values.Select(edit => edit.Candidate));
        this.ConsumeCompleted();
    }

    /// <summary>Applies completed edits without waiting or scheduling filesystem checks.</summary>
    public void ConsumeCompleted()
    {
        this.cache.ConsumeCompleted();
        foreach (var (location, edit) in this.pending.ToArray())
        {
            if (!this.locations.Contains(location) || location.Path != edit.AcceptedPath)
            {
                this.pending.Remove(location);
                continue;
            }

            // A submission needs its completed check even if the UI consumes it after the refresh interval.
            var error = this.GetError(edit.Candidate, location, this.cache.GetStatus(edit.Candidate));
            if (error == DevPluginLocationError.Pending)
                continue;

            this.pending.Remove(location);
            if (error == DevPluginLocationError.None)
            {
                this.drafts.Remove(location);
                location.Path = edit.Candidate;
                this.Changed = true;
            }
            else
            {
                this.ResetInputPath(location);
                this.rejected[location] = (edit.Candidate, error);
            }
        }

        this.UpdatePaths();
    }

    public string GetInputPath(DevPluginLocationSettings location)
        => this.drafts.GetValueOrDefault(location, location.Path);

    /// <summary>Gets the ImGui ID revision used to discard retained native input text.</summary>
    /// <param name="location">The edited location.</param>
    /// <returns>The current input revision.</returns>
    public int GetInputRevision(DevPluginLocationSettings location)
        => this.inputRevisions.GetValueOrDefault(location);

    /// <summary>Updates a draft, cancelling its submission and clearing rejection feedback only if the text changes.</summary>
    /// <param name="location">The edited location.</param>
    /// <param name="path">The current input text.</param>
    public void SetInputPath(DevPluginLocationSettings location, string path)
    {
        if (path == this.GetInputPath(location))
            return;

        this.drafts[location] = path;
        this.pending.Remove(location);
        this.rejected.Remove(location);
        this.UpdatePaths();
    }

    /// <summary>Submits a changed draft for a fresh check while preserving the accepted path.</summary>
    /// <param name="location">The edited location.</param>
    public void SubmitEdit(DevPluginLocationSettings location)
    {
        this.pending.Remove(location);
        this.rejected.Remove(location);
        var candidate = this.GetInputPath(location);
        if (candidate != location.Path)
        {
            this.pending[location] = new(location.Path, candidate);
            this.cache.Invalidate(candidate);
        }

        this.UpdatePaths();
        this.Refresh();
    }

    /// <summary>Clears a submission and its rejection feedback without discarding the draft.</summary>
    /// <param name="location">The edited location.</param>
    public void CancelPendingEdit(DevPluginLocationSettings location)
    {
        this.pending.Remove(location);
        this.rejected.Remove(location);
        this.UpdatePaths();
    }

    /// <summary>Restores the accepted input and prevents outstanding checks from committing the edit.</summary>
    /// <param name="location">The edited location.</param>
    public void DiscardEdit(DevPluginLocationSettings location)
    {
        this.ResetInputPath(location);
        this.CancelPendingEdit(location);
    }

    public string? GetPendingPath(DevPluginLocationSettings location)
        => this.pending.TryGetValue(location, out var edit) ? edit.Candidate : null;

    /// <summary>Combines the accepted path's cached filesystem status with the current list's duplicate check.</summary>
    /// <param name="location">The accepted location.</param>
    /// <returns>The first applicable warning.</returns>
    public DevPluginLocationError GetWarning(DevPluginLocationSettings location)
        => this.GetError(location.Path, location, this.cache.GetStatus(location.Path));

    public (string Path, DevPluginLocationError Error)? GetRejectedEdit(DevPluginLocationSettings location)
        => this.rejected.TryGetValue(location, out var edit) ? edit : null;

    /// <summary>Adds a location after syntax and duplicate checks, without accessing the filesystem.</summary>
    /// <param name="path">The path, optionally enclosed in double quotes.</param>
    /// <returns>The syntax or duplicate error, or <see cref="DevPluginLocationError.None"/> on success.</returns>
    /// <remarks>Additions compare paths case-insensitively; edits and warnings use ordinal comparison.</remarks>
    public DevPluginLocationError Add(string path)
    {
        path = path.Trim('"');
        if (this.locations.Any(location => string.Equals(location.Path, path, StringComparison.InvariantCultureIgnoreCase)))
            return DevPluginLocationError.Duplicate;

        if (!DevPluginPathValidationCache.IsValidPath(path))
            return DevPluginLocationError.InvalidPath;

        this.locations.Add(new DevPluginLocationSettings { Path = path, IsEnabled = true });
        this.Changed = true;
        this.UpdatePaths();
        return DevPluginLocationError.None;
    }

    public void Remove(DevPluginLocationSettings location)
    {
        if (!this.locations.Remove(location))
            return;

        this.drafts.Remove(location);
        this.inputRevisions.Remove(location);
        this.pending.Remove(location);
        this.rejected.Remove(location);
        this.Changed = true;
        this.UpdatePaths();
    }

    private void ResetInputPath(DevPluginLocationSettings location)
    {
        this.drafts.Remove(location);
        // Changing the ID restores the accepted path even if ImGui retains an active or deactivated buffer.
        this.inputRevisions[location] = this.GetInputRevision(location) + 1;
    }

    // Filesystem errors take precedence; duplicates are evaluated against the current accepted locations.
    private DevPluginLocationError GetError(string path, DevPluginLocationSettings location, DevPluginPathStatus status)
        => status switch
        {
            DevPluginPathStatus.Pending => DevPluginLocationError.Pending,
            DevPluginPathStatus.DllUnavailable => DevPluginLocationError.DllUnavailable,
            DevPluginPathStatus.InvalidPath => DevPluginLocationError.InvalidPath,
            DevPluginPathStatus.ManifestUnavailable => DevPluginLocationError.ManifestUnavailable,
            _ => this.locations.Any(other => !ReferenceEquals(other, location) && other.Path == path)
                     ? DevPluginLocationError.Duplicate
                     : DevPluginLocationError.None,
        };

    private void UpdatePaths()
        => this.cache.SetPaths(this.locations.Select(location => location.Path).Concat(this.pending.Values.Select(edit => edit.Candidate)));

    private sealed record PendingEdit(string AcceptedPath, string Candidate);
}
