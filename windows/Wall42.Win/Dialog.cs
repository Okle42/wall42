using System.Runtime.InteropServices;

namespace Wall42;

/// TaskDialogIndirect without WinForms (wall42 stays a plain Win32 process). Needs comctl32 v6, which
/// app.manifest asks for. Only used by the installer: first-run question, done / failed.
static unsafe class Dialog
{
    public static bool Ask(string title, string heading, string text, string yes) =>
        Show(title, heading, text, TD_INFORMATION_ICON, yes) == ID_YES;

    public static void Info(string title, string heading, string text) => Show(title, heading, text, TD_SHIELD_OK_ICON, null);
    public static void Error(string title, string heading, string text) => Show(title, heading, text, TD_ERROR_ICON, null);

    const int ID_YES = 100, IDOK = 1;
    const int TDCBF_OK_BUTTON = 0x1, TDCBF_CANCEL_BUTTON = 0x8;
    const int TDF_ALLOW_DIALOG_CANCELLATION = 0x8, TDF_SIZE_TO_CONTENT = 0x1000000;
    static readonly IntPtr TD_ERROR_ICON = (IntPtr)0xFFFE, TD_INFORMATION_ICON = (IntPtr)0xFFFD, TD_SHIELD_OK_ICON = (IntPtr)0xFFF8;

    static int Show(string title, string heading, string text, IntPtr icon, string? yes)
    {
        var strings = new List<IntPtr>();
        IntPtr S(string s) { var p = Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        try
        {
            var button = new TASKDIALOG_BUTTON { nButtonID = ID_YES, pszButtonText = yes == null ? IntPtr.Zero : S(yes) };
            var c = new TASKDIALOGCONFIG
            {
                cbSize = (uint)sizeof(TASKDIALOGCONFIG),
                dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT,
                dwCommonButtons = yes == null ? TDCBF_OK_BUTTON : TDCBF_CANCEL_BUTTON,
                pszWindowTitle = S(title),
                pszMainIcon = icon,
                pszMainInstruction = S(heading),
                pszContent = S(text),
                cButtons = yes == null ? 0u : 1u,
                pButtons = yes == null ? IntPtr.Zero : (IntPtr)(&button),
                nDefaultButton = yes == null ? IDOK : ID_YES,
            };
            int hr = TaskDialogIndirect(ref c, out var pressed, IntPtr.Zero, IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"TaskDialogIndirect 0x{hr:X8}");
            return pressed;
        }
        finally { foreach (var p in strings) Marshal.FreeHGlobal(p); }
    }

    // commctrl.h declares both with #include <pshpack1.h>: packed on x64 too
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TASKDIALOG_BUTTON { public int nButtonID; public IntPtr pszButtonText; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent, hInstance;
        public int dwFlags, dwCommonButtons;
        public IntPtr pszWindowTitle, pszMainIcon, pszMainInstruction, pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText, pszExpandedInformation, pszExpandedControlText, pszCollapsedControlText,
            pszFooterIcon, pszFooter, pfCallback, lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll")]
    static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button, IntPtr radio, IntPtr verification);
}
