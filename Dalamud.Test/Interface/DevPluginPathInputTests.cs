using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Interface.Internal.Windows.Settings.Widgets;

using Xunit;

namespace Dalamud.Test.Interface;

/// <summary>
/// Verifies path editing and restoration against ImGui's retained native text buffer.
/// </summary>
[Collection("Native framework render synchronization")]
public class DevPluginPathInputTests
{
    private const string OriginalPath = @"C:\plugins\original.dll";
    private const string CandidatePath = @"C:\plugins\candidate.dll";
    private const string NewerPath = @"C:\plugins\newer.dll";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);
    private static readonly Lazy<bool> NativeLibraries = new(() => PrepareNativeLibraries());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectingARefocusedCandidateRestoresTheVisiblePathAndRetainsTheRejection(bool manifestUnavailable)
    {
        using var fixture = new InputFixture();
        fixture.Focus();
        fixture.ReplaceText(CandidatePath);
        fixture.PressKey(ImGuiKey.Enter);
        fixture.WaitForCandidate();
        Assert.Equal(CandidatePath, fixture.Editor.GetPendingPath(fixture.Row));
        Assert.True(fixture.Editor.HasPendingEdits);

        // Refocusing without typing leaves the submitted candidate in ImGui's active buffer.
        fixture.Focus();
        Assert.Equal(CandidatePath, fixture.NativeText);
        var candidateInputId = fixture.InputId;
        var status = manifestUnavailable ? DevPluginPathStatus.ManifestUnavailable : DevPluginPathStatus.DllUnavailable;
        var error = manifestUnavailable ? DevPluginLocationError.ManifestUnavailable : DevPluginLocationError.DllUnavailable;
        fixture.CompleteCandidate(status);
        fixture.WaitForCandidateObservation(status);

        fixture.Frame();

        // Changing the input ID draws the accepted path without reusing ImGui's retained candidate.
        Assert.NotEqual(candidateInputId, fixture.InputId);
        Assert.False(fixture.InputActive);
        Assert.Equal(OriginalPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Equal(OriginalPath, fixture.Row.Path);
        Assert.Equal(CandidatePath, fixture.Editor.GetRejectedEdit(fixture.Row)?.Path);
        Assert.Equal(error, fixture.Editor.GetRejectedEdit(fixture.Row)?.Error);
        Assert.False(fixture.Editor.HasPendingEdits);
        Assert.False(fixture.Editor.Changed);

        var restoredInputId = fixture.InputId;
        fixture.Frame();
        Assert.Equal(restoredInputId, fixture.InputId);
        Assert.False(fixture.InputActive);
        Assert.Equal(OriginalPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Equal(error, fixture.Editor.GetRejectedEdit(fixture.Row)?.Error);

        fixture.Focus();
        Assert.Equal(OriginalPath, fixture.NativeText);
        Assert.Equal(error, fixture.Editor.GetRejectedEdit(fixture.Row)?.Error);

        // Refocusing preserves the rejection; typing a new draft clears it.
        fixture.ReplaceText(NewerPath);
        Assert.Equal(NewerPath, fixture.NativeText);
        Assert.Equal(NewerPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Null(fixture.Editor.GetRejectedEdit(fixture.Row));
        Assert.Equal(OriginalPath, fixture.Row.Path);
        Assert.False(fixture.Editor.HasPendingEdits);
    }

    [Fact]
    public void TypingANewerPathCancelsThePendingSubmissionBeforeItsProbeFinishes()
    {
        using var fixture = new InputFixture();
        fixture.Focus();
        fixture.ReplaceText(CandidatePath);
        fixture.PressKey(ImGuiKey.Enter);
        fixture.WaitForCandidate();
        Assert.True(fixture.Editor.HasPendingEdits);

        fixture.Focus();
        fixture.ReplaceText(NewerPath);

        Assert.Equal(NewerPath, fixture.NativeText);
        Assert.Equal(NewerPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Null(fixture.Editor.GetPendingPath(fixture.Row));
        Assert.False(fixture.Editor.HasPendingEdits);
        Assert.Equal(OriginalPath, fixture.Row.Path);
        Assert.False(fixture.Editor.Changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingAnUnsubmittedEditRestoresTheAcceptedInput(bool escape)
    {
        using var fixture = new InputFixture();
        fixture.Focus();
        fixture.ReplaceText(CandidatePath);
        Assert.Equal(CandidatePath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.False(fixture.Editor.HasPendingEdits);

        if (escape)
            fixture.PressKey(ImGuiKey.Escape);
        else
            fixture.ClickOtherControl();
        fixture.Frame();

        Assert.False(fixture.InputActive);
        Assert.Equal(OriginalPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Equal(OriginalPath, fixture.Row.Path);
        Assert.Null(fixture.Editor.GetPendingPath(fixture.Row));
        Assert.Null(fixture.Editor.GetRejectedEdit(fixture.Row));
        Assert.False(fixture.Editor.Changed);
        fixture.Focus();
        Assert.Equal(OriginalPath, fixture.NativeText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HidingAnUnsubmittedDraftRestoresTheAcceptedInputWhenTheFieldReturns(bool previouslySubmitted)
    {
        using var fixture = new InputFixture();
        fixture.Focus();
        fixture.ReplaceText(CandidatePath);
        if (previouslySubmitted)
        {
            fixture.PressKey(ImGuiKey.Enter);
            fixture.WaitForCandidate();
            fixture.Focus();
            fixture.ReplaceText(NewerPath);
            Assert.Null(fixture.Editor.GetPendingPath(fixture.Row));
        }

        var draftInputId = fixture.InputId;
        Assert.NotEqual(OriginalPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.False(fixture.Editor.HasPendingEdits);

        // Search can omit the widget long enough for ImGui's deactivation event to expire.
        for (var i = 0; i < 3; i++)
            fixture.Frame(drawInput: false);

        fixture.Frame();
        Assert.False(fixture.InputActive);
        Assert.Equal(OriginalPath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Equal(OriginalPath, fixture.Row.Path);
        Assert.Null(fixture.Editor.GetPendingPath(fixture.Row));
        Assert.Null(fixture.Editor.GetRejectedEdit(fixture.Row));
        Assert.False(fixture.Editor.Changed);

        fixture.Frame();
        Assert.NotEqual(draftInputId, fixture.InputId);
        fixture.Focus();
        Assert.Equal(OriginalPath, fixture.NativeText);
    }

    [Fact]
    public void HidingASubmittedCandidatePreservesItsPendingCheckAndAcceptsSuccess()
    {
        using var fixture = new InputFixture();
        fixture.Focus();
        fixture.ReplaceText(CandidatePath);
        fixture.PressKey(ImGuiKey.Enter);
        fixture.WaitForCandidate();
        var submittedInputId = fixture.InputId;

        for (var i = 0; i < 3; i++)
            fixture.Frame(drawInput: false);

        fixture.Frame();
        Assert.False(fixture.InputActive);
        Assert.Equal(submittedInputId, fixture.InputId);
        Assert.Equal(CandidatePath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Equal(CandidatePath, fixture.Editor.GetPendingPath(fixture.Row));
        Assert.True(fixture.Editor.HasPendingEdits);
        Assert.Equal(OriginalPath, fixture.Row.Path);

        fixture.CompleteCandidate(DevPluginPathStatus.Valid);
        fixture.WaitForCandidateObservation(DevPluginPathStatus.Valid);
        fixture.Frame();

        Assert.Equal(submittedInputId, fixture.InputId);
        Assert.Equal(CandidatePath, fixture.Row.Path);
        Assert.Equal(CandidatePath, fixture.Editor.GetInputPath(fixture.Row));
        Assert.Null(fixture.Editor.GetPendingPath(fixture.Row));
        Assert.Null(fixture.Editor.GetRejectedEdit(fixture.Row));
        Assert.False(fixture.Editor.HasPendingEdits);
        Assert.True(fixture.Editor.Changed);
        fixture.Focus();
        Assert.Equal(CandidatePath, fixture.NativeText);
    }

    private static bool PrepareNativeLibraries([CallerFilePath] string sourceFile = "")
    {
        var configuration = typeof(DevPluginPathInputTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var nativeDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "bin", configuration));
        var bindingDirectory = Path.GetDirectoryName(typeof(ImGui).Assembly.Location)!;
        // Native build outputs must be available beside the managed bindings for DLL resolution.
        foreach (var name in new[] { "cimgui.dll", "cimplot.dll", "cimguizmo.dll" })
        {
            var destination = Path.Combine(bindingDirectory, name);
            if (!File.Exists(destination))
                File.Copy(Path.Combine(nativeDirectory, name), destination);
        }

        return true;
    }

    private sealed class InputFixture : IDisposable
    {
        private readonly ImGuiContextPtr previousContext;
        private readonly ImGuiContextPtr context;
        private readonly TaskCompletionSource candidateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<DevPluginPathStatus> candidateResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DevPluginPathValidationCache cache;
        private Vector2 otherControlCenter;

        public InputFixture()
        {
            _ = NativeLibraries.Value;
            this.previousContext = ImGui.GetCurrentContext();
            this.context = ImGui.CreateContext();
            ImGui.SetCurrentContext(this.context);
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(800, 600);
            io.DeltaTime = 1f / 60;
            // Keep the fixture from reading or writing imgui.ini.
            Marshal.WriteIntPtr(Unsafe.As<ImGuiIOPtr, nint>(ref io), (int)Marshal.OffsetOf<ImGuiIO>("IniFilename"), nint.Zero);
            io.Fonts.AddFontDefault();
            io.Fonts.Build();

            this.cache = new DevPluginPathValidationCache(path =>
            {
                if (path != CandidatePath)
                    return DevPluginPathStatus.Valid;

                this.candidateEntered.TrySetResult();
                return this.candidateResult.Task.WaitAsync(WorkerTimeout).GetAwaiter().GetResult();
            });
            this.Editor = new DevPluginLocationEditor(this.cache);
            this.Editor.Reset([new DevPluginLocationSettings { Path = OriginalPath, IsEnabled = true }]);
            this.Frame();
        }

        public DevPluginLocationEditor Editor { get; }

        public DevPluginLocationSettings Row => this.Editor.Locations[0];

        public uint InputId { get; private set; }

        public bool InputActive { get; private set; }

        public string NativeText
        {
            get
            {
                var bytes = ImGui.GetCurrentContext().InputTextState.TextA.ToArray();
                var terminator = Array.IndexOf(bytes, (byte)0);
                return Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
            }
        }

        public void Frame(bool focus = false, bool drawInput = true)
        {
            this.Editor.ConsumeCompleted();
            this.Editor.Refresh();
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(600, 400));
            ImGui.Begin("Dev plugin path input regression", ImGuiWindowFlags.NoSavedSettings);
            if (drawInput)
            {
                if (focus)
                    ImGui.SetKeyboardFocusHere();
                DevPluginsSettingsEntry.DrawPathInput(this.Editor, this.Row);
                this.InputId = ImGuiP.GetItemID();
                this.InputActive = ImGui.IsItemActive();
            }
            ImGui.Button("Other control");
            this.otherControlCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            ImGui.End();
            ImGui.Render();
        }

        public void Focus()
        {
            this.Frame(focus: true);
            this.Frame();
            Assert.True(this.InputActive);
        }

        public void ReplaceText(string text)
        {
            var io = ImGui.GetIO();
            io.AddKeyEvent(ImGuiKey.ModCtrl, true);
            io.AddKeyEvent(ImGuiKey.A, true);
            this.Frame();
            io.AddKeyEvent(ImGuiKey.A, false);
            io.AddKeyEvent(ImGuiKey.ModCtrl, false);
            foreach (var character in text)
                io.AddInputCharacter(character);
            this.Frame();
            // ImGui's event queue may defer text behind the modifier release for one frame.
            this.Frame();
            Assert.Equal(text, this.NativeText);
        }

        public void PressKey(ImGuiKey key)
        {
            ImGui.GetIO().AddKeyEvent(key, true);
            this.Frame();
            ImGui.GetIO().AddKeyEvent(key, false);
            this.Frame();
        }

        public void ClickOtherControl()
        {
            var io = ImGui.GetIO();
            io.AddMousePosEvent(this.otherControlCenter.X, this.otherControlCenter.Y);
            this.Frame();
            io.AddMouseButtonEvent(0, true);
            this.Frame();
            io.AddMouseButtonEvent(0, false);
            this.Frame();
        }

        public void WaitForCandidate()
            => Assert.True(this.candidateEntered.Task.Wait(WorkerTimeout, TestContext.Current.CancellationToken), "The candidate probe did not start.");

        public void CompleteCandidate(DevPluginPathStatus status) => this.candidateResult.TrySetResult(status);

        public void WaitForCandidateObservation(DevPluginPathStatus status)
            => Assert.True(SpinWait.SpinUntil(() =>
            {
                this.cache.ConsumeCompleted();
                return this.cache.GetStatus(CandidatePath) == status;
            }, WorkerTimeout), "The candidate observation did not finish.");

        public void Dispose()
        {
            this.candidateResult.TrySetResult(DevPluginPathStatus.Valid);
            ImGui.DestroyContext(this.context);
            ImGui.SetCurrentContext(this.previousContext);
        }
    }
}
