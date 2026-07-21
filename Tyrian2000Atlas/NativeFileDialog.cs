using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace T2A;

/// <summary>
/// Windows common file dialogs: the folder chooser behind the data-folder Browse button,
/// and the Save-As box behind the PNG export / playback screenshot. Both are IFileDialog
/// derivatives, so they share the vtable slots below (3 = Show, 9/10 = Set/GetOptions,
/// 12 = SetFolder, 15 = SetFileName, 17 = SetTitle, 20 = GetResult, 22 = SetDefaultExtension).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NativeFileDialog
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint FosFileMustExist = 0x1000;
    private const uint SigdnFileSystemPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid ClsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IidFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>One entry of the dialog's file-type dropdown (COMDLG_FILTERSPEC).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterSpec { public IntPtr Name; public IntPtr Spec; }

    // Unmanaged for the process lifetime (never freed): SetFileTypes is not documented to copy
    // the strings, and a `fixed` block would let the GC move them out from under the dialog.
    private static readonly IntPtr PngTypeName = Marshal.StringToCoTaskMemUni("PNG image (*.png)");
    private static readonly IntPtr PngTypeSpec = Marshal.StringToCoTaskMemUni("*.png");

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static string? PickFolderBlocking(string? initialDir, IntPtr owner,
        string title = "Select your Tyrian 2000 folder")
    {
        return RunDialog(in ClsidFileOpenDialog, in IidFileOpenDialog, initialDir, owner, title,
            FosPickFolders | FosForceFileSystem | FosPathMustExist, null);
    }

    /// <summary>The Save-As box, pre-filled with <paramref name="defaultName"/> (".png" enforced).</summary>
    /// <summary>The file type offered follows <paramref name="defaultName"/>'s extension.</summary>
    public static string? SaveFileBlocking(string? initialDir, string defaultName, IntPtr owner,
        string title = "Save PNG")
    {
        return RunDialog(in ClsidFileSaveDialog, in IidFileSaveDialog, initialDir, owner, title,
            FosOverwritePrompt | FosForceFileSystem, defaultName);
    }

    /// <summary>The Open box, for choosing a file that must already exist (the SoundFont picker).</summary>
    public static string? OpenFileBlocking(string? initialDir, IntPtr owner, string title)
    {
        return RunDialog(in ClsidFileOpenDialog, in IidFileOpenDialog, initialDir, owner, title,
            FosFileMustExist | FosForceFileSystem | FosPathMustExist, null);
    }

    /// <param name="saveName">null = folder chooser; otherwise the pre-filled PNG file name.</param>
    private static string? RunDialog(in Guid classId, in Guid interfaceId, string? initialDir,
        IntPtr owner, string title, uint extraOptions, string? saveName)
    {
        int initResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        ThrowIfFailed(initResult);

        try
        {
            IntPtr dialog = IntPtr.Zero;
            ThrowIfFailed(CoCreateInstance(in classId, IntPtr.Zero, ClsctxInprocServer,
                in interfaceId, out dialog));

            try
            {
                uint options;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, 10))(dialog, &options));
                options |= extraOptions;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, 9))(dialog, options));

                fixed (char* titlePtr = title)
                    ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 17))(dialog, titlePtr));

                if (saveName != null) SetUpSave(dialog, saveName);
                SetInitialFolder(dialog, initialDir);

                int showResult = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 3))(dialog, owner);
                if (showResult == ErrorCancelled) return null;
                ThrowIfFailed(showResult);

                IntPtr item = IntPtr.Zero;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dialog, 20))(dialog, &item));
                try
                {
                    IntPtr pathPtr = IntPtr.Zero;
                    ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(item, 5))(
                        item, SigdnFileSystemPath, &pathPtr));
                    try { return Marshal.PtrToStringUni(pathPtr); }
                    finally { Marshal.FreeCoTaskMem(pathPtr); }
                }
                finally { Release(item); }
            }
            finally { Release(dialog); }
        }
        finally
        {
            CoUninitialize();
        }
    }

    /// <summary>
    /// Offer one file type, append its extension for a name typed without one, pre-fill the
    /// box. The type is taken from the default name's own extension, so the same Save-As
    /// serves the PNG exports, the audio ones and the datacube readings.
    /// </summary>
    private static void SetUpSave(IntPtr dialog, string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) ext = "png";
        string label = ext switch
        {
            "wav" => "WAVE audio (*.wav)",
            "mid" => "MIDI file (*.mid)",
            "md" => "Markdown document (*.md)",
            _ => "PNG image (*.png)",
        };

        IntPtr namePtr2 = Marshal.StringToCoTaskMemUni(label);
        IntPtr specPtr = Marshal.StringToCoTaskMemUni("*." + ext);
        try
        {
            var spec = new FilterSpec { Name = namePtr2, Spec = specPtr };
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint, FilterSpec*, int>)Slot(dialog, 4))(
                dialog, 1, &spec));
            fixed (char* extPtr = ext)
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 22))(dialog, extPtr));
            fixed (char* namePtr = fileName)
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 15))(dialog, namePtr));
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr2);
            Marshal.FreeCoTaskMem(specPtr);
        }
    }

    private static void SetInitialFolder(IntPtr dialog, string? initialDir)
    {
        if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir)) return;

        IntPtr item = IntPtr.Zero;
        if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, in IidShellItem, out item) < 0) return;
        try
        {
            // Failure here should not prevent the chooser from opening.
            _ = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 12))(dialog, item);
        }
        finally { Release(item); }
    }

    private static IntPtr Slot(IntPtr instance, int index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    private static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
            _ = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, 2))(instance);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context,
        in Guid interfaceId, out IntPtr instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext,
        in Guid interfaceId, out IntPtr item);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
