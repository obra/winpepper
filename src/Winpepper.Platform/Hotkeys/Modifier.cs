namespace Winpepper.Platform.Hotkeys;

[Flags]
public enum Modifier
{
    None        = 0,
    LeftCtrl    = 1 << 0,
    RightCtrl   = 1 << 1,
    LeftShift   = 1 << 2,
    RightShift  = 1 << 3,
    LeftAlt     = 1 << 4,
    RightAlt    = 1 << 5,
    LeftWin     = 1 << 6,
    RightWin    = 1 << 7,

    Ctrl        = LeftCtrl | RightCtrl,
    Shift       = LeftShift | RightShift,
    Alt         = LeftAlt  | RightAlt,
    Win         = LeftWin  | RightWin,
}
