# Windows 11 glass host

The launcher uses a transparent WPF foreground HWND and a separate, non-activating native HWND immediately behind it. The native window hosts rounded Windows Composition visuals filled with `Compositor.CreateHostBackdropBrush()`. The visual layer samples the desktop behind the launcher; the WPF foreground supplies text, controls, translucent tint, shadows, and genuinely transparent gaps. Mica is wallpaper-derived and does not provide the requested live desktop backdrop.

## Why two windows

The original single-window implementation requested `DWMWA_SYSTEMBACKDROP_TYPE=38` (`DWMSBT_TRANSIENTWINDOW=3`) on a non-layered WPF HWND and applied a union rounded `HRGN`. On Windows 11 build 26300 at 150% scaling, the real preview HWND read back backdrop type 3 and a correct complex region, but the material still painted the **entire rectangular 990×123 window**. A checkerboard test using only our own windows measured black and white gap pixels both becoming gray. `DWMWA_REDIRECTIONBITMAP_ALPHA=39` also returned success without fixing the pixels. Those return codes cannot establish a shaped glass surface.

A second probe created `CreateDesktopWindowTarget` on the same WPF HWND. With `DWMWA_USE_HOSTBACKDROPBRUSH=17` and a full DWM frame, its rounded visual sampled and softened the checkerboard, but WPF's surface left a white rectangular gap and the Composition visual covered a red WPF foreground mark. This matches WPF/Win32 airspace limitations. The two-window probe then used an `WS_EX_NOREDIRECTIONBITMAP` native backdrop HWND and a layered WPF foreground. Its gap pixels remained exactly black/white, its interior pixels became softened shades, and the WPF red mark and rounded border remained visible. `WindowFromPoint` hit the checkerboard through empty gaps, never the backdrop helper.

## Current implementation

`CompositionGlassHost` creates an `WS_POPUP` helper with `WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST`. It enables `DWMWA_USE_HOSTBACKDROPBRUSH`, creates a WinRT `Compositor` desktop target using the C#/WinRT ABI pointer, and adds one clipped sprite per visible `GlassRegionPart`. The sprite bounds and radii come from the current WPF layout in physical pixels, so scaling and the launcher's spring motion are reflected in the material. The helper is placed immediately behind its foreground through `SetWindowPos`; it is hidden with the foreground and destroyed on close. There is no idle rendering timer. Each WPF UI thread keeps its DispatcherQueue controller until dispatcher shutdown, allowing settings and launcher hosts to coexist or be reopened.

`WindowsGlassService` activates this path on Windows 11 build 22621+ when composition and transparency effects are enabled and high contrast is off. If creation fails or policy changes, it destroys the helper and keeps the existing solid WPF material. Windows 10 also uses that solid fallback. The launcher already observes reduced-motion settings for its animation. Windows can alter host-backdrop appearance for power, contrast, or graphics policy; the code does not promise a fixed blur radius. `CreateHostBackdropBrush` is the desktop sampler, not a configurable Gaussian blur filter.

Native and WPF layers share a physical rectangle, but neither draws an opaque full-window background. The foreground's `AllowsTransparency=True` is essential. The native visual clips each capsule, circle, result panel, or help card separately. WPF `DropShadowEffect` remains on the foreground, so edges may lightly tint adjacent gaps; deep gaps must still show the desktop.

## Verification on this machine

`dotnet run --project Tests/Luma.SmokeTests.csproj -c Debug -- --glass-native` passed on Windows build 26300 at 150% scaling. It opens only its own non-activating checkerboard and preview windows, reads pixels only inside that checkerboard, and closes them. The actual `MainWindow` composite is saved at `Tests/bin/Debug/net10.0-windows10.0.19041.0/glass-main-composite.png`; Apple Light and help views use `glass-main-light-composite.png` and `glass-main-help-composite.png`. The actual Settings composite is `glass-settings-composite.png` in the same directory. These are **desktop-composited** screen captures, not offscreen `RenderTargetBitmap` renders.

The smoke checks black and white gap samples, reduced but nonzero black/white contrast inside the empty center of the search capsule, helper and foreground bounds, helper HWND styles, direct z-order adjacency, input hit testing, move/resize/show-animation/hide-show/help transitions, same-STA host coexistence and recreation, and helper destruction after window close. The isolated proof in `G:\AI\coding\13-launcher-glass-probe` also records point-by-point samples in `dual-results.json` and an independent `dual-composite.png`.

This is strong evidence for the tested desktop/DPI/palette. A user check on the installed launcher is still valuable for different wallpaper, monitors, accessibility settings, graphics policy, and battery state. The smoke never modifies the installed program or the user's real configuration.

## Primary references

- [Windows Composition with Win32 desktop HWNDs](https://learn.microsoft.com/en-us/windows/uwp/composition/using-the-visual-layer-with-win32)
- [`Compositor.CreateHostBackdropBrush`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)
- [C#/WinRT COM interop guide](https://github.com/microsoft/CsWinRT/blob/master/docs/interop.md)
- [Microsoft's Win32 Composition samples](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples)
- [WPF and Win32 airspace behavior](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-win32-interoperation)
- [Windows 11 Acrylic guidance](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [Mica wallpaper material](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-mica-win32)
