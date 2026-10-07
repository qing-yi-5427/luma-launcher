# Windows 11 glass host

The launcher and Settings window request **Desktop Acrylic** from DWM on Windows 11 build 22621 or later. This is the system material for transient surfaces and can include the wallpaper and other windows behind the launcher. Mica is an opaque, wallpaper-derived material, so it would not meet the requested live background blur. WPF's own `BlurEffect` only blurs WPF content and is not a desktop backdrop.

## Implementation

`MainWindow` uses one ordinary, non-layered WPF HWND (`AllowsTransparency=False`). `WindowsGlassService` sets the WPF composition target's background to transparent, extends the DWM frame across the client area, and requests `DWMWA_SYSTEMBACKDROP_TYPE=38` with `DWMSBT_TRANSIENTWINDOW=3`. A union of native rounded regions (`SetWindowRgn`) limits this HWND to the search capsule, visible circular buttons, results panel, and help card. Thus the empty space between these surfaces is outside the window region. The regions are rebuilt using the current WPF visual bounds and display DPI when the layout or spring position changes. The service has no permanent render-frame subscription while the launcher is idle or hidden.

The launcher uses a translucent local material brush only when the native Acrylic request is accepted. It uses a solid brush on Windows 10, when composition or system transparency is unavailable, or under high contrast. Windows can also make Acrylic solid for battery saver, low-end hardware, inactive windows, or policy. The launcher already honors `SystemParameters.ClientAreaAnimation` for reduced motion.

`SettingsWindow` uses its own non-layered HWND and one rounded native region. Its sidebar brush becomes translucent when Acrylic is available; the right content pane remains opaque for legibility. Both windows set `DWMWA_USE_IMMERSIVE_DARK_MODE=20` from the effective Luma palette so manually selecting Apple Dark or Apple Light also updates the native material appearance.

Native window regions and per-pixel alpha windows do not receive DWM's automatic corner rounding. Our round-rect HRGN supplies the outline. The existing WPF `DropShadowEffect` can be clipped at the HRGN edge, so outer shadow quality needs desktop visual review. A full-size render PNG cannot validate this material or its shadow because it has no desktop pixels behind it.

`DesktopAcrylicController` is another official route, but it requires the Windows App SDK and a `DispatcherQueue`/composition target. The DWM attribute route fits this WPF HWND without a new runtime. `SetWindowCompositionAttribute`/`ACCENT_ENABLE_ACRYLICBLURBEHIND` was not selected as the supported Windows 11 API. The documented `DwmEnableBlurBehindWindow` does not produce blur on Windows 8 and later, so Windows 10 uses the solid fallback.

## Evidence and limits

On this machine (`10.0.26300`), the isolated .NET 10 WPF probe in `G:\AI\coding\13-launcher-glass-probe` returned `0x00000000` from full-client `DwmExtendFrameIntoClientArea`, `DwmSetWindowAttribute(38, 3)`, and `DwmGetWindowAttribute(38)`; the latter read back `3`. Its non-layered union-region HWND returned `1` from `SetWindowRgn` and `3` (complex region) from `GetWindowRgn`. A layered WPF probe also accepted attribute 38 and read back 3, which demonstrates why HRESULT alone cannot prove visible Acrylic on a layered window.

`dotnet run --project Tests/Luma.SmokeTests.csproj -c Release -- --glass-native` passes on this machine. It checks a real, offscreen, non-activating `MainWindow` after `Show()` and during `AnimateShow(false)`: the HWND has no `WS_EX_LAYERED` bit at start, in five animation samples, or afterward. When system transparency is available it reads back backdrop type 3. The complex region survives animation; a point inside the help card enters the region when the card appears and leaves it when the card closes. It also checks a real offscreen `SettingsWindow` is non-layered with a rounded region and verifies both windows request native dark mode for Apple Dark. The test never sends input, focuses another app, or changes the installed launcher. The separate probe also found that setting an ordinary WPF window's `Window.Opacity=0.9` did not add `WS_EX_LAYERED` before or after offscreen `Show()` on this build; the production fade now changes the root visual's opacity.

These checks establish API acceptance, HWND style, and region shape. They **do not establish visible blur of real desktop pixels**. Final acceptance requires opening the launcher over distinct background windows on a Windows 11 desktop, confirming those windows are visibly blurred through both the search capsule and result panel, and checking the gaps, corners, shadows, theme contrast, high contrast, transparency-off, scaling, and motion behavior.

## Primary references

- [DWM system backdrop types and Windows 11 build requirement](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [WPF's own DWM backdrop implementation notes](https://github.com/dotnet/wpf/blob/main/Documentation/docs/using-fluent.md#backdrop-support-in-wpf)
- [WPF `AllowsTransparency` behavior](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency)
- [WPF regions and layered windows](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/technology-regions-overview)
- [Windows 11 Acrylic policy and performance](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
- [Mica uses wallpaper as an opaque material](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-mica-win32)
- [Regions and per-pixel alpha prevent system corner rounding](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)
- [Windows App SDK `DesktopAcrylicController` HWND target requirements](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.systembackdrops.desktopacryliccontroller.settarget)
- [`DwmEnableBlurBehindWindow` no longer blurs starting with Windows 8](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow)
