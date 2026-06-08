namespace WFInfo
{
    /// <summary>
    /// Warframe UI themes that the OCR can detect.
    /// Values must match index into ThemePrimary/ThemeSecondary color arrays.
    /// </summary>
    public enum WFtheme : int
    {
        VITRUVIAN = 0,
        STALKER = 1,
        BARUUK = 2,
        CORPUS = 3,
        FORTUNA = 4,
        GRINEER = 5,
        LOTUS = 6,
        NIDUS = 7,
        OROKIN = 8,
        TENNO = 9,
        HIGH_CONTRAST = 10,
        LEGACY = 11,
        EQUINOX = 12,
        DARK_LOTUS = 13,
        ZEPHYR = 14,
        CONQUERA = 15,
        DEADLOCK = 16,
        LUNAR_RENEWAL = 17,
        POM_2 = 18,
        UNKNOWN = -1,
        AUTO = -2,
        CUSTOM = -3
    }

    public enum Display
    {
        Window,
        Overlay,
        Light
    }

}