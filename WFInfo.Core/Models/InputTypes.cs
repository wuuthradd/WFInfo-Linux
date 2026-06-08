namespace WFInfo.Models
{
    /// <summary>
    /// Cross-platform key representation (replaces System.Windows.Input.Key)
    /// </summary>
    public enum VirtualKey
    {
        None = 0,
        // Letters
        A = 44, B = 45, C = 46, D = 47, E = 48, F = 49, G = 50, H = 51,
        I = 52, J = 53, K = 54, L = 55, M = 56, N = 57, O = 58, P = 59,
        Q = 60, R = 61, S = 62, T = 63, U = 64, V = 65, W = 66, X = 67,
        Y = 68, Z = 69,
        // Numbers
        D0 = 34, D1 = 35, D2 = 36, D3 = 37, D4 = 38, D5 = 39,
        D6 = 40, D7 = 41, D8 = 42, D9 = 43,
        // Function keys
        F1 = 90, F2 = 91, F3 = 92, F4 = 93, F5 = 94, F6 = 95,
        F7 = 96, F8 = 97, F9 = 98, F10 = 99, F11 = 100, F12 = 101,
        // Modifiers
        LeftShift = 116, RightShift = 117, LeftCtrl = 118, RightCtrl = 119,
        LeftAlt = 120, RightAlt = 121,
        // Special
        Space = 18, Enter = 6, Escape = 13, Tab = 3, Back = 2,
        Delete = 32, Insert = 31, Home = 22, End = 21,
        PageUp = 19, PageDown = 20,
        Left = 23, Up = 24, Right = 25, Down = 26,
        PrintScreen = 30,
        // Numpad
        NumPad0 = 74, NumPad1 = 75, NumPad2 = 76, NumPad3 = 77,
        NumPad4 = 78, NumPad5 = 79, NumPad6 = 80, NumPad7 = 81,
        NumPad8 = 82, NumPad9 = 83,
        // OEM keys
        OemTilde = 130, OemMinus = 131, OemPlus = 132,
        OemOpenBrackets = 133, OemCloseBrackets = 134,
        OemPipe = 135, OemSemicolon = 136, OemQuotes = 137,
        OemComma = 138, OemPeriod = 139, OemSlash = 140,
        OemBackslash = 141,
    }

    /// <summary>
    /// Cross-platform mouse button representation
    /// </summary>
    public enum VirtualMouseButton
    {
        Left = 0,
        Middle = 1,
        Right = 2,
        XButton1 = 3,
        XButton2 = 4
    }
}