using System.Windows.Forms;

namespace EmotePreviewer.App.Services;

/// <summary>
/// Shows the Windows folder picker on a dedicated STA thread. The browser cannot reveal real paths, so the settings
/// page asks the server to pick a folder instead.
/// </summary>
public sealed class FolderDialog
{
    readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Returns the chosen folder, or null when the user cancelled or a dialog is already open.</summary>
    public async Task<string?> PickAsync(string? initial, string? title, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        UseDescriptionForTitle = true,
                        Description = string.IsNullOrWhiteSpace(title) ? "EmotePreviewer" : title,
                        ShowNewFolderButton = false,
                    };
                    if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
                    using var owner = new ForegroundOwner();
                    var result = dialog.ShowDialog(owner);
                    tcs.TrySetResult(result == DialogResult.OK ? dialog.SelectedPath : null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            { IsBackground = true, Name = "folder-dialog" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>A hidden top-most window so the dialog appears in front of the browser instead of behind it.</summary>
    sealed class ForegroundOwner : Form, IWin32Window
    {
        public ForegroundOwner()
        {
            TopMost = true;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new System.Drawing.Size(1, 1);
            Location = new System.Drawing.Point(-2000, -2000);
            Opacity = 0;
            Show();
            Activate();
        }
    }
}
