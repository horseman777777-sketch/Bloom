using System;

namespace Bloom.WinKey;

/// One low-level keyboard hook's view of the Win key: a Win key pressed and released on its own is a tap; Win with
/// any other key, or with Shift/Ctrl/Alt already held, is a combination and left to Windows. Bloom's own hook and
/// the elevated helper's both keep one.
internal sealed class WinTap
{
    public const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;

    private uint winDown;
    private bool combo;

    /// The Win key held now, as this hook saw it (0: none).
    public uint WinDown => winDown;

    /// One key event. `own`: injected by a hook (WinKeyProtocol.OwnTag). True: a tap just ended — the caller
    /// swallows this release and replays it behind the mask key.
    public bool Key(uint vk, bool down, bool up, bool own, Func<bool> modifiersHeld)
    {
        if (vk is not (VK_LWIN or VK_RWIN))
        {
            if (!own && down && winDown != 0) combo = true;
            return false;
        }
        if (own)
        {
            // the other hook took the real release and replayed it: this one never sees that release
            if (up) winDown = 0;
            return false;
        }
        if (down)
        {
            if (winDown == 0)
            {
                winDown = vk;
                combo = modifiersHeld();
            }
            else if (vk != winDown) combo = true;
            return false;
        }
        if (!up || vk != winDown) return false;
        winDown = 0;
        return !combo;
    }
}
