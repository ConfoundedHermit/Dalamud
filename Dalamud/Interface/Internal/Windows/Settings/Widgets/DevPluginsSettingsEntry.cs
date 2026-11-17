using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using CheapLoc;

using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Internal;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

/// <summary>
/// Edits dev plugin load locations and displays asynchronous path validation results.
/// </summary>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal sealed class DevPluginsSettingsEntry : SettingsEntry
{
    private readonly FileDialogManager fileDialogManager = new();
    private readonly DevPluginLocationEditor editor = new();
    private readonly Func<bool>? checkVisibility;

    private string devPluginTempLocation = string.Empty;
    private string devPluginLocationAddError = string.Empty;
    private long addErrorTimestamp;
    private bool hadDevPlugins;

    public DevPluginsSettingsEntry(Func<bool>? visibility = null)
    {
        this.Name = LazyLoc.Localize("DalamudSettingsDevPluginLocation", "Dev Plugin Locations");
        this.checkVisibility = visibility;
    }

    public override bool IsVisible => this.IsDevModeEnabled || this.hadDevPlugins;

    // Filesystem warnings on accepted locations do not block saving; unresolved path changes do.
    public override bool IsValid => !this.editor.HasPendingEdits;

    private bool IsDevModeEnabled => this.checkVisibility?.Invoke() ?? true;

    /// <summary>Advances submitted edits while the settings window is open, including when this field is hidden.</summary>
    public void AdvancePendingEdits() => this.editor.AdvancePendingEdits();

    public override void OnClose()
    {
        this.Load();
    }

    public override void Load()
    {
        this.editor.Reset(Service<DalamudConfiguration>.Get().DevPluginLoadLocations);
        this.devPluginLocationAddError = string.Empty;
        this.devPluginTempLocation = string.Empty;
        if (this.editor.Locations.Count > 0)
            this.hadDevPlugins = true;
    }

    public override void Save()
    {
        if (!this.IsValid)
            return;

        var config = Service<DalamudConfiguration>.Get();

        config.DevPluginLoadLocations.RemoveAll(existing =>
                                                    this.editor.Locations.All(loc => loc.Path != existing.Path));

        foreach (var pendingLocation in this.editor.Locations)
        {
            var existing = config.DevPluginLoadLocations
                                 .FirstOrDefault(x => x.Path == pendingLocation.Path);

            if (existing != null)
            {
                existing.IsEnabled = pendingLocation.IsEnabled;
                existing.Nickname = pendingLocation.Nickname;
            }
            else
            {
                config.DevPluginLoadLocations.Add(pendingLocation);
            }
        }

        if (this.editor.Changed)
        {
            _ = Service<PluginManager>.Get().ScanDevPluginsAsync();
            this.editor.MarkSaved();
        }
    }

    public override void Draw()
    {
        using var id = ImRaii.PushId("devPluginLocation"u8);

        this.editor.ConsumeCompleted();
        if (!this.IsDevModeEnabled)
        {
            this.DrawPendingEditMessage();
            ImGui.TextColoredWrapped(
                ImGuiColors.AttentionForeground,
                LazyLoc.Localize(
                    "DalamudSettingDevModeDisabledWithDevPlugins",
                    "Developer Mode is disabled, but dev plugins were loaded during this session.\n" +
                    "They will stay loaded, but will not show in the installer next time the game is restarted."));
            return;
        }

        this.editor.Refresh();
        ImGui.Text(this.Name);

        if (this.editor.Changed)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
            {
                ImGui.SameLine();
                ImGui.Text(Loc.Localize("DalamudSettingsChanged", "(Changed)"));
            }
        }

        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsDevPluginLocationsHint", "Add dev plugin load locations.\nThis must be a path to the plugin DLL."));
        this.DrawPendingEditMessage();

        var locationSelect = Loc.Localize("DalamudDevPluginLocationSelect", "Select Dev Plugin DLL");
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Folder, locationSelect))
        {
            this.fileDialogManager.OpenFileDialog(
                locationSelect,
                ".dll",
                (result, path) =>
                {
                    if (result)
                    {
                        this.devPluginTempLocation = path;
                        this.AddDevPlugin();
                    }
                });
        }

        ImGuiHelpers.ScaledDummy(5);

        ImGui.Columns(5);
        ImGui.SetColumnWidth(0, 18 + (5 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(1, ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - (18 + 16 + 14 + 14) - ((5 + 45 + 26 + 120) * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(2, 16 + (120 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(3, 16 + (45 * ImGuiHelpers.GlobalScale));
        ImGui.SetColumnWidth(4, 14 + (26 * ImGuiHelpers.GlobalScale));

        ImGui.Separator();

        ImGui.Text("#"u8);
        ImGui.NextColumn();
        ImGui.Text("Path"u8);
        ImGui.NextColumn();
        ImGui.Text("Nickname"u8);
        ImGui.NextColumn();
        ImGui.Text("Enabled"u8);
        ImGui.NextColumn();
        ImGui.Text(string.Empty);
        ImGui.NextColumn();

        ImGui.Separator();

        DevPluginLocationSettings locationToRemove = null;

        var locNumber = 1;
        foreach (var devPluginLocationSetting in this.editor.Locations)
        {
            var isEnabled = devPluginLocationSetting.IsEnabled;

            // Keep the row identity stable when its accepted path changes.
            id.Push(RuntimeHelpers.GetHashCode(devPluginLocationSetting));

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 8 - (ImGui.CalcTextSize(locNumber.ToString()).X / 2));
            ImGui.Text(locNumber.ToString());
            ImGui.NextColumn();

            var warning = this.editor.GetWarning(devPluginLocationSetting);
            var hasError = warning != DevPluginLocationError.None;
            var error = GetLocationError(warning);

            ImGui.SetNextItemWidth(!hasError ? -1 : ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
            DrawPathInput(this.editor, devPluginLocationSetting);

            if (hasError)
            {
                ImGui.SameLine();
                ImGuiComponents.HelpMarker(
                    error.Loc.ToString(),
                    error.Icon,
                    error.Color ?? Colors.ImGuiColors.WarningForeground);
            }

            if (this.editor.GetPendingPath(devPluginLocationSetting) is not null)
            {
                ImGui.TextColoredWrapped(
                    ImGuiColors.DalamudGrey,
                    Loc.Localize("DalamudDevPluginEditCurrentPath", "Current path: {0}").Format(devPluginLocationSetting.Path));
                ImGui.TextColoredWrapped(
                    ImGuiColors.AttentionForeground,
                    Loc.Localize("DalamudDevPluginEditChecking", "Checking the submitted path..."));
            }

            if (this.editor.GetRejectedEdit(devPluginLocationSetting) is { } rejected)
            {
                ImGui.TextColoredWrapped(
                    ImGuiColors.WarningForeground,
                    Loc.Localize("DalamudDevPluginEditRejected", "Path change rejected: {0}\n{1}")
                       .Format(rejected.Path, GetLocationError(rejected.Error).Loc.ToString()));
            }

            ImGui.NextColumn();

            ImGui.SetNextItemWidth(-1);
            var nickname = devPluginLocationSetting.Nickname ?? string.Empty;
            if (ImGui.InputTextWithHint("##devPluginNickname", "Optional...", ref nickname, 64))
            {
                devPluginLocationSetting.Nickname = string.IsNullOrEmpty(nickname) ? null : nickname;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Localize("DalamudDevPluginNicknameHint", "Optional nickname shown next to the plugin name in the plugin list."));

            ImGui.NextColumn();

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 7 - (12 * ImGuiHelpers.GlobalScale));
            ImGui.Checkbox("##devPluginLocationCheck"u8, ref isEnabled);
            ImGui.NextColumn();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
            {
                locationToRemove = devPluginLocationSetting;
            }

            id.Pop();

            ImGui.NextColumn();
            ImGui.Separator();

            devPluginLocationSetting.IsEnabled = isEnabled;

            locNumber++;
        }

        if (locationToRemove != null)
        {
            this.editor.Remove(locationToRemove);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() / 2) - 8 - (ImGui.CalcTextSize(locNumber.ToString()).X / 2));
        ImGui.Text(locNumber.ToString());
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##devPluginLocationInput"u8, ref this.devPluginTempLocation, 300);
        ImGui.NextColumn();
        // New locations start enabled without a nickname.
        ImGui.NextColumn();
        ImGui.NextColumn();
        if (!string.IsNullOrEmpty(this.devPluginTempLocation) && ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
        {
            this.AddDevPlugin();
        }

        ImGui.Columns(1);

        if (!string.IsNullOrEmpty(this.devPluginLocationAddError) && Stopwatch.GetElapsedTime(this.addErrorTimestamp) < TimeSpan.FromSeconds(5))
        {
            ImGui.TextColoredWrapped(new Vector4(1, 0, 0, 1), this.devPluginLocationAddError);
        }
    }

    public override void PostDraw()
    {
        this.fileDialogManager.Draw();
    }

    /// <summary>Tracks draft text each frame and submits path changes on Enter.</summary>
    /// <param name="editor">The current settings session.</param>
    /// <param name="location">The edited location.</param>
    internal static void DrawPathInput(DevPluginLocationEditor editor, DevPluginLocationSettings location)
    {
        using var id = ImRaii.PushId(editor.GetInputRevision(location));
        var path = editor.GetInputPath(location);
        var submitted = ImGui.InputText("##devPluginLocationInput"u8, ref path, 65535, ImGuiInputTextFlags.EnterReturnsTrue);

        // EnterReturnsTrue still copies text each frame. Track changes so refocusing preserves pending edits and rejections.
        editor.SetInputPath(location, path);
        if (submitted)
            editor.SubmitEdit(location);
        // Search may hide the deactivation frame; discard abandoned drafts when the input reappears.
        else if (ImGui.IsItemDeactivatedAfterEdit() ||
                 (!ImGui.IsItemActive() && path != location.Path && editor.GetPendingPath(location) is null))
            editor.DiscardEdit(location);
    }

    private static LocationError GetLocationError(DevPluginLocationError error)
        => error switch
        {
            DevPluginLocationError.Pending => new(
                LazyLoc.Localize("DalamudDevPluginLocationChecking", "Checking path..."),
                FontAwesomeIcon.Clock,
                ImGuiColors.DalamudGrey),
            DevPluginLocationError.DllUnavailable => new(LazyLoc.Localize("DalamudDevPluginLocationFileDoesNotExist", "File does not exist.")),
            DevPluginLocationError.InvalidPath => new(LazyLoc.Localize("DalamudDevPluginInvalid", "The entered value is not a valid path to a potential Dev Plugin.\nDid you mean to enter it as a custom plugin repository in the fields below instead?")),
            DevPluginLocationError.ManifestUnavailable => new(LazyLoc.Localize("DalamudDevPluginLocationManifestDoesNotExist", "Manifest does not exist.")),
            DevPluginLocationError.Duplicate => new(LazyLoc.Localize("DalamudDevPluginLocationExists", "Location already exists.")),
            _ => default,
        };

    private void AddDevPlugin()
    {
        var error = this.editor.Add(this.devPluginTempLocation);
        if (error == DevPluginLocationError.None)
        {
            this.hadDevPlugins = true;
            this.devPluginTempLocation = string.Empty;
        }
        else
        {
            this.devPluginLocationAddError = GetLocationError(error).Loc.ToString();
            this.addErrorTimestamp = Stopwatch.GetTimestamp();
            if (error == DevPluginLocationError.InvalidPath)
                return;
        }

        // Enable ImGui assertions by default for dev plugin use.
        Service<DalamudConfiguration>.Get().ImGuiAssertsEnabledAtStartup ??= true;
    }

    private void DrawPendingEditMessage()
    {
        if (!this.editor.HasPendingEdits)
            return;

        ImGui.TextColoredWrapped(
            ImGuiColors.AttentionForeground,
            Loc.Localize("DalamudDevPluginEditSavePending", "A Dev Plugin path change is being checked. Save is unavailable until the check finishes."));
        if (ImGui.SmallButton(Loc.Localize("DalamudDevPluginEditCancel", "Cancel pending path changes")))
        {
            foreach (var location in this.editor.Locations)
            {
                if (this.editor.GetPendingPath(location) is null)
                    continue;

                this.editor.DiscardEdit(location);
            }
        }
    }

    private record struct LocationError(LazyLoc Loc, FontAwesomeIcon Icon = FontAwesomeIcon.ExclamationTriangle, Vector4? Color = null);
}
