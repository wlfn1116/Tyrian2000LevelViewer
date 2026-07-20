using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace T2LV;

/// <summary>Windows folder chooser used by the data-folder Browse button.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class NativeFolderDialog
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosPathMustExist = 0x800;
    private const uint SigdnFileSystemPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    public static string? PickBlocking(string? initialDir, IntPtr owner,
        string title = "Select your Tyrian 2000 folder")
    {
        int initResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        ThrowIfFailed(initResult);

        try
        {
            IntPtr dialog = IntPtr.Zero;
            ThrowIfFailed(CoCreateInstance(in ClsidFileOpenDialog, IntPtr.Zero, ClsctxInprocServer,
                in IidFileOpenDialog, out dialog));

            try
            {
                uint options;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, 10))(dialog, &options));
                options |= FosPickFolders | FosForceFileSystem | FosPathMustExist;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, 9))(dialog, options));

                fixed (char* titlePtr = title)
                    ThrowIfFailed(((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 17))(dialog, titlePtr));

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
